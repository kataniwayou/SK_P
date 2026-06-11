using BaseConsole.Core.Messaging;
using global::Keeper;
using global::Keeper.Recovery;
using MassTransit;
using MassTransit.Middleware;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// KEEP-05 / D-01 (SustainedOutage mode) — the INVERSE of <see cref="RecoveryDeadLetterFacts"/>. Under
/// <see cref="ExhaustionPolicy.SustainedOutage"/> (D-03) a faulting recovery op must be HELD/REDELIVERED with
/// NO dead-letter — the message keeps cycling in-process (the accepted poison-op spin) and is NEVER moved to
/// the consolidated <c>skp-dlq-1</c>. This wires the real <see cref="ReinjectConsumer"/> on an in-memory
/// <see cref="ITestHarness"/> with a throwing L2 (infra fault), applying the SAME endpoint config the
/// production <see cref="RecoveryEndpointBinder"/> uses for the SustainedOutage branch — an UNBOUNDED interval
/// retry (no <c>Immediate</c>-then-dead-letter) — and asserts:
/// <list type="number">
///   <item>NO <see cref="ConsolidatedFault"/> is produced within a bounded window (the inverse of the Dlq1
///   fact's positive ConsolidatedFault assertion — OQ-2 functional gate).</item>
///   <item>The consumer is invoked MORE THAN ONCE (the L2 read is retried/redelivered) — proving the message
///   is held/redelivered rather than acked-and-dropped, so genuine work is not silently lost (T-52-06).</item>
/// </list>
/// <para>
/// The bounded <see cref="CancellationTokenSource"/> is REQUIRED (D-03): SustainedOutage requeues forever, so
/// the fact must NOT rely on the default infinite test token — it asserts "no dead-letter within the window +
/// at least one redelivery", then stops the harness. The consolidated error sink + <c>ConfigureError</c>
/// pipeline are still wired (exactly as BaseConsole.Core wires them globally per-endpoint), so the assertion
/// proves the no-dead-letter outcome comes from the UNBOUNDED retry never exhausting, NOT from the error move
/// being absent. Hermetic scope: in-memory proves the no-dead-letter SHAPE, not broker-literal requeue depth
/// (Phase-54 Manual-Only).
/// </para>
/// </summary>
public sealed class SustainedOutageFacts
{
    /// <summary>An L2 whose StringLengthAsync raises a Redis INFRASTRUCTURE exception (the op-exhaustion path)
    /// AND counts how many times the consumer's read was invoked, so a redelivery is observable.</summary>
    private static (IConnectionMultiplexer Mux, Func<int> ReadCount) ThrowingCountingMux()
    {
        var count = 0;
        var db = Substitute.For<IDatabase>();
        db.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<long>>(_ =>
            {
                Interlocked.Increment(ref count);
                throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "L2 down (sustained outage)");
            });
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return (mux, () => Volatile.Read(ref count));
    }

    private static ServiceProvider BuildHarness(IConnectionMultiplexer mux) =>
        new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .AddSingleton(mux)
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>))
            .AddSingleton<global::Keeper.Observability.KeeperMetrics>()
            .AddSingleton(Options.Create(new RetryOptions { Limit = 1 }))
            .AddSingleton(Options.Create(new RecoveryOptions
            {
                PartitionCount = 8,
                ExhaustionPolicy = ExhaustionPolicy.SustainedOutage,
            }))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<ReinjectConsumer>();

                // The consolidated forensic sink so a moved ConsolidatedFault WOULD be observable if one were
                // produced (the negative assertion is meaningful only because this sink is wired).
                x.AddHandler((ConsumeContext<ConsolidatedFault> _) => Task.CompletedTask)
                    .Endpoint(e => e.Name = ConsolidatedErrorTransportFilter.Dlq1);

                // The SustainedOutage SUT pipeline — the SAME shape RecoveryEndpointBinder applies for the
                // SustainedOutage branch: an UNBOUNDED interval retry (NO Immediate-then-dead-letter) so a
                // thrown delivery is held/redelivered and never exhausts to the error transport. The
                // consolidated error MOVE is still wired (BaseConsole.Core wires it globally per-endpoint), so
                // the no-dead-letter outcome is proven to come from the retry never exhausting. A short 150ms
                // interval keeps the test fast (production uses 1s); the mode behavior is identical.
                x.AddConfigureEndpointsCallback((context, name, e) =>
                {
                    // Large-but-finite count (NOT int.MaxValue — that OOMs MassTransit's pre-allocated
                    // TimeSpan[]; the production binder uses the same large-finite shape via
                    // RecoveryEndpointBinder.SustainedOutageRetryCount). 10,000 × 500ms ≈ 1.4h — far beyond
                    // this ~3s window, so within the window the retry never exhausts (no dead-letter). The
                    // 500ms interval keeps the in-flight consume mostly in a CANCELLABLE inter-attempt delay
                    // so the bounded harness.Stop can force-cancel the accepted spin cleanly.
                    e.UseMessageRetry(r => r.Interval(10_000, TimeSpan.FromMilliseconds(500)));
                    e.ConfigureError(ep =>
                    {
                        ep.UseFilter(new GenerateFaultFilter());
                        ep.UseFilter(new ConsolidatedErrorTransportFilter());
                    });
                });

                x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
            })
            .BuildServiceProvider(true);

    [Fact]
    [Trait("Phase", "52")]
    public async Task SustainedOutage_holds_and_redelivers_no_dead_letter()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mux, readCount) = ThrowingCountingMux();
        await using var provider = BuildHarness(mux);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var msg = new KeeperReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
            {
                CorrelationId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                EntryId = Guid.NewGuid(),
                Payload = "step-config",
            };
            await harness.Bus.Publish(msg, ct);

            // BOUNDED window (D-03): SustainedOutage requeues forever, so we must NOT wait on the infinite test
            // token. Within this window the unbounded interval retry must NOT have produced a ConsolidatedFault
            // (no dead-letter), and the read must have been retried at least twice (held/redelivered).
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(3));

            // NO dead-letter: no ConsolidatedFault lands in the consolidated sink within the bounded window
            // (the inverse of the Dlq1 fact's Assert.True(...Any<ConsolidatedFault>...)).
            Assert.False(
                await harness.Consumed.Any<ConsolidatedFault>(window.Token),
                "SustainedOutage must NOT dead-letter — no ConsolidatedFault may be produced (the message is held/redelivered).");

            // Held/redelivered, not acked-and-dropped: the throwing L2 read was invoked more than once
            // (T-52-06 — genuine work is not silently lost during the outage).
            Assert.True(
                readCount() > 1,
                $"SustainedOutage must redeliver the faulting op (read invoked {readCount()} time(s); expected > 1).");
        }
        finally
        {
            // SustainedOutage spins forever, so a graceful harness.Stop would BLOCK waiting for the in-flight
            // (still-retrying) consume to finish. Stop with a BOUNDED token so MassTransit force-cancels the
            // in-flight delivery (the consume is interruptible while sitting in the retry's inter-attempt
            // delay) rather than hanging on the accepted spin (D-03).
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await harness.Stop(stopCts.Token); }
            catch (OperationCanceledException) { /* force-stop the accepted spin — expected */ }
        }
    }
}
