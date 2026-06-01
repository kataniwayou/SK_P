using System.Text.Json;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BaseProcessor.Core.Liveness;

/// <summary>
/// The only-when-Healthy liveness heartbeat (LIVE-01..06). A <see cref="BackgroundService"/> that, every
/// <see cref="ProcessorLivenessOptions.IntervalSeconds"/>, writes/refreshes the FROZEN
/// <see cref="ProcessorProjection"/> to the shared <c>skp:{processorId}</c> key (via
/// <see cref="L2ProjectionKeys.Processor(System.Guid)"/>) with a sliding TTL — so the unchanged v3.4.0
/// <c>ProcessorLivenessValidator</c> reads the processor as live.
///
/// <para>
/// <b>Healthy gate (LIVE-04 / T-26-09):</b> a beat writes ONLY when
/// <see cref="IProcessorContext.IsHealthy"/> and the Id is populated. A not-yet-Healthy replica no-ops the
/// tick (writes nothing) so the orchestrator sees it as <c>absent</c>.
/// </para>
/// <para>
/// <b>Sliding SET (LIVE-02/06):</b> each beat refreshes the timestamp from the injected
/// <see cref="TimeProvider"/> and re-applies the configured TTL via a blind whole-value
/// <c>StringSetAsync(..., expiry: TtlSeconds)</c> — last-write-wins, NO lock / read-modify-write.
/// </para>
/// <para>
/// <b>Interval in SECONDS (LIVE-03):</b> the written <c>liveness.interval</c> equals
/// <see cref="ProcessorLivenessOptions.IntervalSeconds"/> (seconds, NOT milliseconds) so the reader's
/// <c>timestamp + interval*2</c> staleness math holds.
/// </para>
/// <para>
/// <b>Resilience (D-11 / T-26-10):</b> a Redis fault on a beat is logged-and-continued — the host never
/// crashes and the loop keeps beating (the soft-dep multiplexer is built with <c>abortConnect=false</c>).
/// </para>
/// </summary>
public sealed class ProcessorLivenessHeartbeat : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IProcessorContext _context;
    private readonly ProcessorLivenessOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProcessorLivenessHeartbeat> _logger;

    public ProcessorLivenessHeartbeat(
        IConnectionMultiplexer redis,
        IProcessorContext context,
        IOptions<ProcessorLivenessOptions> options,
        TimeProvider clock,
        ILogger<ProcessorLivenessHeartbeat> logger)
    {
        _redis   = redis ?? throw new ArgumentNullException(nameof(redis));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _clock   = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger  = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options;
        var period = TimeSpan.FromSeconds(opts.IntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Healthy gate (LIVE-04 / T-26-09) — only a Healthy, identified replica writes; a
            // not-yet-Healthy replica no-ops this tick (it does NOT wait — a no-op tick is correct).
            if (_context.IsHealthy && _context.Id is { } id)
            {
                try
                {
                    // SAME clock the reader uses — mirrors RedisProjectionWriter.cs:60 and the
                    // ProcessorLivenessValidator `now` read (Pitfall 2).
                    var now = _clock.GetUtcNow().UtcDateTime;

                    // REUSE the frozen records (D-09); interval written in SECONDS (LIVE-03, NOT
                    // milliseconds); status is the shared LivenessStatus.Healthy const (never a literal).
                    var projection = new ProcessorProjection(
                        _context.InputDefinition,
                        _context.OutputDefinition,
                        new LivenessProjection(now, opts.IntervalSeconds, LivenessStatus.Healthy));

                    var json = JsonSerializer.Serialize(projection);
                    var db = _redis.GetDatabase();

                    // Sliding SET..EX (LIVE-02): blind whole-value SET, no lock/RMW (LIVE-06). The key
                    // is built via L2ProjectionKeys.Processor — never a literal.
                    //
                    // BY DESIGN: stoppingToken is deliberately NOT threaded into the write (StringSetAsync
                    // has no CancellationToken overload). Shutdown does not cancel an in-flight write — a
                    // hung-but-not-dead Redis bounds shutdown latency by StackExchange.Redis' own command
                    // timeout, NOT by stoppingToken. The token is observed only at the Task.Delay below.
                    // This keeps the D-11 log-and-continue contract simple (no OperationCanceledException
                    // disambiguation in the catch); the command timeout is the intended upper bound.
                    await db.StringSetAsync(
                        L2ProjectionKeys.Processor(id),
                        json,
                        expiry: TimeSpan.FromSeconds(opts.TtlSeconds));
                }
                catch (Exception ex)
                {
                    // Resilience (D-11 / T-26-10): log-and-CONTINUE; never throw, never return. A dead
                    // Redis must not crash the host (soft-dep abortConnect=false).
                    _logger.LogWarning(
                        ex,
                        "Liveness write failed for processor {ProcessorId}; will retry next beat",
                        _context.Id);
                }
            }

            try
            {
                await Task.Delay(period, _clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
