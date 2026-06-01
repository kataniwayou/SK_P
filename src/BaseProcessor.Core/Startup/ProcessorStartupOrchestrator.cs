using BaseConsole.Core.Health;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaseProcessor.Core.Startup;

/// <summary>
/// The processor's startup brain (IDENT-04 / SCHEMA-01/02 / RPC-04) — the near-exact mirror of
/// <c>Orchestrator/Hydration/HydrationBackgroundService</c>, adapted for the dual-response
/// <c>IRequestClient</c> round-trip with an UNBOUNDED retry curve (boot-before-register tolerated).
///
/// <para>
/// <b>Loop A (identity):</b> resolve the processor identity by SourceHash via
/// <c>IRequestClient&lt;GetProcessorBySourceHash&gt;</c>; retry on BOTH
/// <c>RequestTimeoutException</c> AND <c>ProcessorIdentityNotFound</c> with bounded exponential
/// backoff (capped at <see cref="ProcessorLivenessOptions.BackoffCapSeconds"/>) until a Found
/// response arrives — then populate the <see cref="IProcessorContext"/>.
/// </para>
/// <para>
/// <b>Loop B (definitions):</b> for each NON-NULL <c>InputSchemaId</c>/<c>OutputSchemaId</c> resolve
/// the definition via <c>IRequestClient&lt;GetSchemaDefinition&gt;</c> with the same unbounded
/// retry/backoff. <c>null</c> schema Ids are SKIPPED (no request sent — SCHEMA-02) and
/// the config schema id is NEVER queried (D-05).
/// </para>
/// <para>
/// <b>Completion (D-02):</b> after identity + all required non-null definitions resolve, call
/// <see cref="IProcessorContext.MarkHealthy"/> then <see cref="IStartupGate.MarkReady"/> — so
/// <c>/startup</c> and <c>/ready</c> flip green HERE, not at bare host start, and the Phase 03
/// heartbeat (gated on <c>IsHealthy</c>) begins writing.
/// </para>
/// <para>
/// <b>Resilience (T-26-04/05):</b> exactly one in-flight request at a time per loop; a short
/// per-request timeout (<see cref="ProcessorLivenessOptions.RequestTimeoutSeconds"/>) fails a
/// slow/absent responder fast and re-loops; NotFound + timeout are caught and looped so the host
/// never crashes while the WebApi responder / DB row is absent. Only shutdown cancellation returns.
/// </para>
/// </summary>
public sealed class ProcessorStartupOrchestrator(
    IRequestClient<GetProcessorBySourceHash> identityClient,
    IRequestClient<GetSchemaDefinition> schemaClient,
    ISourceHashProvider sourceHash,
    IProcessorContext context,
    IStartupGate gate,
    IOptions<ProcessorLivenessOptions> options,
    TimeProvider clock,
    ILogger<ProcessorStartupOrchestrator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        var cap = TimeSpan.FromSeconds(opts.BackoffCapSeconds);

        // --- Loop A: identity-by-SourceHash (UNBOUNDED retry; only break on Found) ---
        var hash = sourceHash.Get();
        var delay = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var resp = await identityClient.GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>(
                    new GetProcessorBySourceHash(hash), stoppingToken,
                    RequestTimeout.After(s: opts.RequestTimeoutSeconds));

                if (resp.Is(out Response<ProcessorIdentityFound>? found))
                {
                    context.SetIdentity(found!.Message);
                    logger.LogInformation("Identity resolved for hash {Hash}: processor {ProcessorId}",
                        hash, found.Message.Id);
                    break;
                }

                // Dual-response NotFound — the DB row is not yet registered (boot-before-register, D-04).
                // Keep looping; do NOT crash the host.
                logger.LogInformation(
                    "Processor row not yet registered for hash {Hash}; retrying in {Delay}", hash, delay);
            }
            catch (RequestTimeoutException)
            {
                logger.LogWarning("Identity request timed out; retrying in {Delay}", delay);
            }

            var next = await BackoffAsync(delay, cap, stoppingToken);
            if (next is not { } d) return; // shutdown requested mid-backoff
            delay = d;
        }

        if (stoppingToken.IsCancellationRequested)
            return;

        // --- Loop B: per-non-null definition (SCHEMA-01/02). Never read the config schema id (D-05). ---
        foreach (var schemaId in new[] { context.InputSchemaId, context.OutputSchemaId })
        {
            if (schemaId is not { } id)
                continue; // null schema id skipped by design — no request sent (SCHEMA-02).

            delay = TimeSpan.FromSeconds(1);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var resp = await schemaClient.GetResponse<SchemaDefinitionFound, SchemaDefinitionNotFound>(
                        new GetSchemaDefinition(id), stoppingToken,
                        RequestTimeout.After(s: opts.RequestTimeoutSeconds));

                    if (resp.Is(out Response<SchemaDefinitionFound>? def))
                    {
                        context.SetDefinition(id, def!.Message.Definition);
                        logger.LogInformation("Definition resolved for schema {SchemaId}", id);
                        break;
                    }

                    logger.LogInformation(
                        "Schema {SchemaId} not yet available; retrying in {Delay}", id, delay);
                }
                catch (RequestTimeoutException)
                {
                    logger.LogWarning("Schema request for {SchemaId} timed out; retrying in {Delay}", id, delay);
                }

                var next = await BackoffAsync(delay, cap, stoppingToken);
                if (next is not { } d) return; // shutdown requested mid-backoff
                delay = d;
            }

            if (stoppingToken.IsCancellationRequested)
                return;
        }

        // --- Completion (D-02): identity + all required non-null definitions resolved. ---
        context.MarkHealthy(); // /startup + /ready meaning of "Healthy" (LIVE-04) — heartbeat may now write.
        gate.MarkReady();      // flip the startup gate HERE, not at host-start.
        logger.LogInformation("Processor reached Healthy; startup gate marked ready.");
    }

    /// <summary>
    /// Cancellation-safe bounded-backoff step: delays for the current <paramref name="delay"/> on the
    /// injected <c>clock</c> (FakeTimeProvider-drivable — 3-arg overload), then returns the
    /// NEXT delay (the current doubled, capped at <paramref name="cap"/>). Returns <c>null</c> when
    /// shutdown was requested mid-delay so the caller can return.
    /// </summary>
    private async Task<TimeSpan?> BackoffAsync(TimeSpan delay, TimeSpan cap, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, cap.TotalSeconds));
    }
}
