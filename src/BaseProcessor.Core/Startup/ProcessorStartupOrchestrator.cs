using BaseConsole.Core.Health;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
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
/// <b>Completion (D-02/D-03 — EXEC-01, the load-bearing order):</b> after identity + all required
/// non-null definitions resolve, bind the dispatch receive endpoint named <c>{Id:D}</c> on the
/// running bus via <c>IReceiveEndpointConnector.ConnectReceiveEndpoint</c>
/// (durable defaults + <c>Immediate(3)</c> retry + <see cref="EntryStepDispatchConsumer"/> attached),
/// <c>await handle.Ready</c>, THEN call <see cref="IProcessorContext.MarkHealthy"/> and
/// <see cref="IStartupGate.MarkReady"/>. Because the heartbeat writes L2 only when <c>IsHealthy</c>,
/// the <c>"Healthy"</c> key necessarily lands in L2 AFTER the bind — so the orchestrator (which admits
/// only Healthy processors) never Sends to a non-existent queue. <c>/startup</c> and <c>/ready</c> flip
/// green HERE, not at bare host start.
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
    IReceiveEndpointConnector endpointConnector,
    MeterProviderHolder meterProviderHolder,
    IOptions<ProcessorLivenessOptions> options,
    IOptions<RetryOptions> retryOptions,
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
                    // MLBL-03 (ii): swap the metrics MeterProvider to the DB-sourced service_name
                    // ({db.Name}_{db.Version}) — synchronous, inside Loop A, BEFORE the {id:D} queue-bind /
                    // MarkHealthy (race-safe: the dispatch counters can't fire until the queue is bound
                    // post-swap; the heartbeat writes Redis only). Reads Name/Version straight off the
                    // received message (WR-03 memory-visibility is moot at the call-site).
                    // WR-02: the metrics label is non-load-bearing for correctness and identity has already
                    // resolved — a swap fault (e.g. ForceFlush on the placeholder provider throwing) must NOT
                    // fault this BackgroundService. Degrade to "keep emitting on the placeholder provider + warn".
                    try
                    {
                        meterProviderHolder.SwapTo($"{found.Message.Name}_{found.Message.Version}");
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Metrics provider swap failed for hash {Hash}; continuing on the placeholder service_name (non-fatal).",
                            hash);
                    }
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

        // --- Completion (D-02/D-03): identity + all required non-null definitions resolved. ---
        // Bind the dispatch receive endpoint BEFORE MarkHealthy (EXEC-01 — the load-bearing order):
        // because the heartbeat writes L2 only when IsHealthy, "Healthy" necessarily lands in L2 AFTER
        // the queue is declared + the consumer attached, so the orchestrator (admits only Healthy) never
        // Sends to a non-existent queue.
        var queueName = $"{context.Id!.Value:D}";   // BARE name (queue: scheme is sender-only) -> competing-consumer
        var retryLimit = retryOptions.Value.Limit;   // D-10: per-process retry budget from the "Retry" section (default Immediate(3))
        var handle = endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) =>
        {
            // D-09 reconcile (Phase 44, Pitfall 1): the in-code ProcessorPipeline RetryLoop now owns EVERY
            // per-op retry (L2 read/write/delete + each send), all bounded by this SAME retryLimit
            // (Retry:Limit). This UseMessageRetry is the OUTER dead-letter LATCH — it fires ONLY when an
            // in-code send-exhaustion PROPAGATES out of the pipeline (D-10), NOT a second retry of the L2
            // ops (those are already surfaced-not-thrown inside RunAsync). Do NOT remove it: it is the
            // _error dead-letter trigger. The shared Limit keeps the two layers from desyncing.
            cfg.UseMessageRetry(r => r.Immediate(retryLimit));        // outer dead-letter latch (D-09/D-10); D-10 config-bound Limit
            cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx);    // DI-resolved consumer attached
        });
        await handle.Ready;                                          // queue declared + consumer attached BEFORE Healthy (D-03)

        context.MarkHealthy(); // NOW IsHealthy opens -> heartbeat's first write lands AFTER the bind (LIVE-04/EXEC-01).
        gate.MarkReady();      // flip the startup gate HERE, not at host-start.
        logger.LogInformation(
            "Dispatch endpoint {Queue} bound; processor reached Healthy; startup gate ready.", queueName);
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
