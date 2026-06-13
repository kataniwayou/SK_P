using BaseConsole.Core.Health;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Liveness;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
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
/// <b>Loop B (definitions):</b> for each NON-NULL <c>InputSchemaId</c>/<c>OutputSchemaId</c>/<c>ConfigSchemaId</c>
/// resolve the definition via <c>IRequestClient&lt;GetSchemaDefinition&gt;</c> with the same unbounded
/// retry/backoff. <c>null</c> schema Ids are SKIPPED (no request sent — SCHEMA-02 / CFG-07 fetch-side).
/// The config schema id IS now queried (D-12 lifts the prior D-05 carve-out) and its definition stored on
/// <see cref="IProcessorContext.ConfigDefinition"/> for Gate A.
/// </para>
/// <para>
/// <b>Gate A (CFG-05/06/07 — D-09/D-11/D-13):</b> AFTER Loop B and BEFORE the dispatch bind, run
/// <see cref="ConfigSchemaCoverageCheck.Evaluate"/> on the fetched <c>ConfigDefinition</c> vs the concrete
/// <c>TConfig</c> (from <see cref="IConfigTypeProvider"/>). A null definition is covered (skip — CFG-07).
/// On a clash the processor STAYS UP but never serves: log ONE Error (D-10), fire
/// <see cref="IStartupGate.MarkReady"/> (no K8s crash-loop — D-09), WITHHOLD
/// <see cref="IProcessorContext.MarkHealthy"/>, do NOT bind the receive endpoint, and return (terminal,
/// no retry — D-11). Because the heartbeat writes L2 only when <c>IsHealthy</c>, a clash means no
/// <c>skp:{id}</c> key → the orchestration-start liveness gate reports "absent" → 422.
/// </para>
/// <para>
/// <b>Completion (D-02/D-03 — EXEC-01, the load-bearing order):</b> on Gate A pass OR a null config id,
/// bind the dispatch receive endpoint named <c>{Id:D}</c> on the running bus via
/// <c>IReceiveEndpointConnector.ConnectReceiveEndpoint</c>
/// (durable defaults + a bare <see cref="EntryStepDispatchConsumer"/> attached — NO bus retry latch,
/// A18/Phase-53 D-01: send-exhaustion throws → broker nack-requeue redelivery, no _error),
/// <c>await handle.Ready</c>, THEN call <see cref="IProcessorContext.MarkHealthy"/> and
/// <see cref="IStartupGate.MarkReady"/>. Because the heartbeat writes L2 only when <c>IsHealthy</c>,
/// the <c>"Healthy"</c> key necessarily lands in L2 AFTER the bind — so the orchestrator (which admits
/// only Healthy processors) never Sends to a non-existent queue. <see cref="IStartupGate.MarkReady"/> fires
/// on BOTH the clash path and the pass/skip path (D-09); <see cref="IProcessorContext.MarkHealthy"/> + the
/// bind fire ONLY on pass/skip. <c>/startup</c> and <c>/ready</c> flip green HERE, not at bare host start.
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
    IConfigTypeProvider configType,
    ProcessorLivenessWriter writer,
    string instanceId,
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

                    // STATE-03 / LOOP-01 / D-02: the FIRST inline unhealthy write — context.Id is now non-null,
                    // so a starting/restarting replica is visible in L2 as `unhealthy` from this first
                    // post-identity iteration (never absent). Subsequent Loop-B iterations + the Gate-A paths
                    // re-write it as per-schema progress advances.
                    await WriteUnhealthyAsync();
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

        // --- Loop B: per-non-null definition (SCHEMA-01/02). Resolve input/output AND config definitions
        // (D-12; the D-05 "never read the config schema id" carve-out is lifted — the config def is Gate A's
        // input, CFG-03/04). A null config id is null-skipped by the existing guard below (CFG-07 fetch-side). ---
        foreach (var schemaId in new[] { context.InputSchemaId, context.OutputSchemaId, context.ConfigSchemaId })
        {
            if (schemaId is not { } id)
                continue; // null schema id skipped by design — no request sent (SCHEMA-02).

            delay = TimeSpan.FromSeconds(1);

            while (!stoppingToken.IsCancellationRequested)
            {
                // LOOP-01 / D-02: rewrite the unhealthy entry at EACH Loop-B iteration so the L2 summary
                // tracks per-schema progress (a still-null definition => Fail, a resolved one => Success)
                // while any non-null schema remains unresolved — status stays Unhealthy. Rides the existing
                // backoff iterations (no new timer — D-03).
                await WriteUnhealthyAsync();

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

        // --- Gate A (CFG-05/06/07 — D-09/D-11/D-13): AFTER Loop B, BEFORE the bind. ---
        // Check the fetched config-schema definition COVERS the concrete TConfig (schema ⊨ TConfig). A null
        // ConfigDefinition (null ConfigSchemaId) is covered → skip (CFG-07). On a clash the processor STAYS UP
        // but never serves: one Error log (D-10), MarkReady (no crash-loop — D-09, Pitfall 1), and a terminal
        // return that WITHHOLDS MarkHealthy + the bind (D-11). The Type is process-stable (Pitfall 4).
        var coverage = ConfigSchemaCoverageCheck.Evaluate(context.ConfigDefinition, configType.Get());
        if (!coverage.Covered)
        {
            logger.LogError(
                "Gate A incompatibility for processor {ProcessorId} config schema {ConfigSchemaId}: {Clash}",
                context.Id, context.ConfigSchemaId, coverage.ClashDetail);
            // LOOP-01 / D-02 / D-04: the terminal-clash replica stays UP but never serves — publish it in L2 as
            // `unhealthy` so the Phase-61 gate fails it on `status`. The config definitions are RESOLVED here, so
            // the naive per-schema Outcome would be all-Success ⇒ Create derives Healthy. The Gate-A outcome is
            // `configSchema` (D-04), so on a clash we feed configOutcome=Fail explicitly — the only way the
            // terminal-unhealthy replica is published as Unhealthy (Create derives status from the summary).
            await WriteUnhealthyAsync(configOutcomeOverride: SchemaOutcome.Fail);
            gate.MarkReady();   // readiness green — NO K8s crash-loop (D-09). MarkHealthy + bind NOT reached.
            return;             // terminal — Gate A is not retried (D-11); the definition is immutable (D-05).
        }

        // --- Completion (D-02/D-03): identity + all required non-null definitions resolved + Gate A passed/skipped. ---
        // Bind the dispatch receive endpoint BEFORE MarkHealthy (EXEC-01 — the load-bearing order):
        // because the heartbeat writes L2 only when IsHealthy, "Healthy" necessarily lands in L2 AFTER
        // the queue is declared + the consumer attached, so the orchestrator (admits only Healthy) never
        // Sends to a non-existent queue.
        var queueName = $"{context.Id!.Value:D}";   // BARE name (queue: scheme is sender-only) -> competing-consumer
        var handle = endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) =>
        {
            // A18 end-state (Phase-53 D-01): NO bus retry latch on the dispatch endpoint. The in-code
            // ProcessorPipeline RetryLoop owns EVERY per-op retry (L2 read/write/delete + each send), bounded
            // by Retry:Limit. A send that exhausts the in-code loop PROPAGATES (throws) out of the pipeline →
            // with neither retry nor an error filter on this endpoint, the default is RabbitMQ nack-requeue
            // (broker redelivery) — no _error, no dead-letter. The Model-B outer UseMessageRetry latch is gone.
            cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx);    // DI-resolved consumer attached (bare tail)
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

    /// <summary>
    /// STATE-03 / LOOP-01 / D-01/02/03/04: write the inline <c>unhealthy</c> per-instance liveness entry for the
    /// CURRENT resolution iteration through the shared <see cref="ProcessorLivenessWriter"/>, so a starting /
    /// restarting / clashed replica is visible in L2 (never absent from the first post-identity iteration).
    ///
    /// <para>
    /// Building the per-schema <c>summary</c> from <c>context</c>'s definition props is safe ONLY here — the
    /// orchestrator owns the single-threaded resolution state (WR-03 forbids reading them from another thread
    /// before <c>IsHealthy</c>). Per-schema outcome mirrors <see cref="ProcessorLivenessEntry.Create"/>'s
    /// null-is-skip: a null schema id ⇒ <see cref="SchemaOutcome.Success"/>; a non-null-but-still-unresolved id
    /// (definition null) ⇒ <see cref="SchemaOutcome.Fail"/>; a resolved definition ⇒
    /// <see cref="SchemaOutcome.Success"/>. <c>configSchema</c> is the Gate-A outcome (D-04): during the
    /// resolution window it tracks resolution like input/output (Open Q1 RESOLVED), and on a Gate-A clash the
    /// caller passes <paramref name="configOutcomeOverride"/> = <see cref="SchemaOutcome.Fail"/> so the terminal
    /// replica is published <see cref="LivenessStatus.Unhealthy"/> (Create derives status from the summary, so a
    /// Fail is the only way to keep an all-resolved-but-clashed replica Unhealthy).
    /// </para>
    /// <para>
    /// D-02 guard: no write before Loop A resolves identity (<c>context.Id</c> null ⇒ no processorId ⇒ no key).
    /// The recorded interval is <see cref="ProcessorLivenessOptions.StartupIntervalSeconds"/> (30, D-12) ⇒ the
    /// writer derives TTL = max(30×2, Ttl-floor) = 60 (D-13). Resilience (LOOP-01) is the shared writer's
    /// log-and-continue — a dead Redis must NOT crash the host or abort resolution.
    /// </para>
    /// </summary>
    private async Task WriteUnhealthyAsync(string? configOutcomeOverride = null)
    {
        if (context.Id is not { } procId) return; // D-02: no processorId before Loop A resolves identity

        static string Outcome(Guid? id, string? def) =>
            id is null ? SchemaOutcome.Success            // null-is-skip ⇒ Success
                       : def is null ? SchemaOutcome.Fail // non-null but not-yet-resolved ⇒ Fail
                                     : SchemaOutcome.Success; // resolved ⇒ Success

        var now = clock.GetUtcNow().UtcDateTime; // SAME clock the reader uses
        var entry = ProcessorLivenessEntry.Create(
            inputOutcome:  Outcome(context.InputSchemaId,  context.InputDefinition),
            outputOutcome: Outcome(context.OutputSchemaId, context.OutputDefinition),
            configOutcome: configOutcomeOverride ?? Outcome(context.ConfigSchemaId, context.ConfigDefinition),
            timestamp:     now,
            interval:      options.Value.StartupIntervalSeconds); // D-12: startup anchor = BackoffCap (30s)

        // Shared writer (Plan 02): SET(perInstance, ttl=max(60,30)=60) + idempotent SADD + L1 Update + log-and-continue.
        await writer.WriteAsync(procId, instanceId, entry);
    }
}
