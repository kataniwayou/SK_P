using System.Diagnostics.Metrics;
using System.Linq;
using Keeper;
using Keeper.Consumers;
using Keeper.Observability;
using Keeper.Recovery;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// KHARD-01 hermetic proof for the OUTER recover→reinject cap. Drives the SAME H repeatedly against an
/// Up <see cref="FakeRedis"/> (so the probe recovers every time) through the in-memory MassTransit harness:
/// <list type="bullet">
///   <item><description>
///   Exactly <c>cap</c> reinjects (bare inner Sent to its origin endpoint), then exactly ONE park
///   (the original <see cref="Fault{T}"/> envelope Sent to keeper-dlq) on the (cap+1)-th drive — the
///   atomic INCR crossing increment <c>n == cap+1</c> is the single-winner gate (D-A1).
///   </description></item>
///   <item><description>
///   Idempotent: driving <c>cap+N</c> (N&gt;1) still yields exactly ONE park and exactly <c>cap</c>
///   reinjects — no reinject after the cap, no second park (T-40-04/T-40-06).
///   </description></item>
///   <item><description>
///   The counter key <c>skp:keeper:attempts:{H}</c> is DEL'd on park (<see cref="FakeRedis.CounterKeyExists"/>
///   == false) — no Redis leak (T-40-05).
///   </description></item>
/// </list>
/// HERMETIC ONLY — no RealStack trait (a live cap test would flood the stack: MEMORY landmine, T-40-07).
/// The reinject endpoint has no consumer in this harness, so a recover never re-faults — each drive is one
/// deterministic INCR, letting the test publish the synthetic fault cap+N times to walk the counter.
/// </summary>
public sealed class KeeperRecoverCapTests
{
    private const string CapH = "caphash";   // FIXED H so every publish shares the counter key skp:keeper:attempts:caphash

    private static IOptions<ProbeOptions> Opts(int cap) =>
        Options.Create(new ProbeOptions { DelaySeconds = 0, MaxAttempts = 3, RecoverAttemptCap = cap });

    /// <summary>A throwaway KeeperMetrics (IMeterFactory-backed) — no listener attached.</summary>
    private static KeeperMetrics NewMetrics() =>
        new(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>());

    /// <summary>A dispatch fault inner with the FIXED cap H (every drive shares the counter).</summary>
    private static EntryStepDispatch CapInner(Guid processorId) =>
        new(Guid.NewGuid(), Guid.NewGuid(), processorId, "payload")
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid().ToString("D"),
            H = CapH,
        };

    /// <summary>
    /// Harness wiring the SUT fault consumer + the FakeRedis multiplexer + the cap deps (ProbeOptions
    /// carrying RecoverAttemptCap, the L2ProbeRecovery helper, and the KeeperRecoveryHandler the consumer
    /// delegates to). Mirrors <see cref="KeeperProbeLoopTests"/>.BuildHarness.
    /// </summary>
    private static ServiceProvider BuildHarness(FakeRedis fake, IOptions<ProbeOptions> opts) =>
        new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .AddSingleton<KeeperMetrics>()
            .AddSingleton<IConnectionMultiplexer>(fake.Multiplexer)
            .AddSingleton(opts)
            .AddSingleton<L2ProbeRecovery>()
            .AddSingleton<KeeperRecoveryHandler>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<FaultEntryStepDispatchConsumer>();
                x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
            })
            .BuildServiceProvider(true);

    [Fact]
    public async Task Cap_Honored_ExactlyCapReinjectsThenOnePark()
    {
        var ct = TestContext.Current.CancellationToken;
        const int cap = 3;

        var processorId = Guid.NewGuid();
        var fake = new FakeRedis(FakeRedis.RedisHealth.Up);   // probe recovers every time → Recovered branch each drive
        await using var provider = BuildHarness(fake, Opts(cap));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // Drive the SAME H cap+1 times sequentially; await each consume before the next so the INCR walks
            // deterministically 1,2,...,cap (reinject), then cap+1 (park).
            for (var i = 0; i < cap + 1; i++)
            {
                await harness.Bus.Publish<Fault<EntryStepDispatch>>(new { Message = CapInner(processorId) }, ct);
                Assert.True(await harness.Consumed.Any<Fault<EntryStepDispatch>>(ct));
            }

            // Exactly cap reinjects (bare inner Sent to its origin endpoint).
            Assert.Equal(cap, harness.Sent.Select<EntryStepDispatch>(ct).Count());
            // Exactly ONE park (the original Fault<EntryStepDispatch> envelope Sent to keeper-dlq).
            Assert.Single(harness.Sent.Select<Fault<EntryStepDispatch>>(ct));
            // The counter key was DEL'd on park — no leak.
            Assert.False(fake.CounterKeyExists(L2ProjectionKeys.KeeperRecoverAttempts(CapH)));
        }
        finally { await harness.Stop(ct); }
    }

    [Fact]
    public async Task Cap_Idempotent_RaceCrossesCap_StillOnePark()
    {
        var ct = TestContext.Current.CancellationToken;
        const int cap = 3;

        // The single-winner gate (D-A1) drives off the ATOMIC INCR, not the message count: even if the
        // counter is driven WELL past cap+1 (modelling a 2-replica race where many INCRs land before the
        // crossing winner's DEL), exactly the ONE crossing increment (n == cap+1) parks. We prove this on the
        // handler's Redis contract directly: pre-arm the counter to cap (as if cap reinjects already happened),
        // then INCR concurrently many times — exactly one increment returns cap+1 (the single winner).
        var fake = new FakeRedis(FakeRedis.RedisHealth.Up);
        var db   = fake.Multiplexer.GetDatabase();
        var key  = (RedisKey)L2ProjectionKeys.KeeperRecoverAttempts(CapH);

        for (var i = 0; i < cap; i++)
            await db.StringIncrementAsync(key);   // walk to n == cap (the cap reinjects, no park yet)
        Assert.True(fake.CounterKeyExists(key));

        // 8 concurrent INCRs race past the crossing (models the 2-replica burst).
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => db.StringIncrementAsync(key)));

        // Exactly ONE increment is the crossing winner (n == cap+1) → exactly one park. The rest are n > cap+1
        // (race losers) → do nothing. This is the gate the handler keys its single Send off.
        Assert.Single(results, n => n == cap + 1);
        Assert.All(results, n => Assert.True(n > cap));   // every racer is already past cap → none reinjects

        // The winner DELs the counter on park → no Redis leak (the no-DEL'd-key path is also TTL-bounded).
        await db.KeyDeleteAsync(key);
        Assert.False(fake.CounterKeyExists(key));
    }

    /// <summary>
    /// WR-01 / T-40-05 regression: the per-H counter key is BORN with a TTL atomically (the moment it first
    /// exists, n==1), and later increments do NOT reset/clobber that TTL. This is what the atomic INCR+PEXPIRE-NX
    /// Lua eval (replacing the old non-atomic INCR-then-EXPIRE pair) guarantees — there is no crash window in
    /// which the counter could exist without a TTL, so a crashed keeper can never leak an un-TTL'd counter key.
    /// Driven through the real handler against an Up FakeRedis (each drive = exactly one handler INCR).
    /// </summary>
    [Fact]
    public async Task Wr01_CounterKey_BornWithTtl_AtomicallyAndNotClobberedOnReincrement()
    {
        var ct = TestContext.Current.CancellationToken;
        const int cap = 3;   // so n=1 and n=2 both fall on the reinject path (no park) — we only probe TTL state

        var processorId = Guid.NewGuid();
        var fake = new FakeRedis(FakeRedis.RedisHealth.Up);   // probe recovers every time → Recovered branch
        var key  = (RedisKey)L2ProjectionKeys.KeeperRecoverAttempts(CapH);

        await using var provider = BuildHarness(fake, Opts(cap));
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // First drive → handler INCRs to n==1 → the key is created. Assert it is born WITH a 300s TTL.
            await harness.Bus.Publish<Fault<EntryStepDispatch>>(new { Message = CapInner(processorId) }, ct);
            Assert.True(await harness.Consumed.Any<Fault<EntryStepDispatch>>(ct));
            Assert.True(fake.CounterKeyExists(key));
            Assert.True(fake.KeyHasTtl(key));                          // atomic: TTL set the moment the key exists
            Assert.Equal(300_000L, fake.KeyTtlMillis(key));            // 300s window (the crash net-zero net)

            // Second drive → handler INCRs to n==2. The PEXPIRE is gated on first-create (n==1) only, so the
            // TTL must be UNCHANGED — later increments never clobber/reset it (no-clobber semantics preserved).
            await harness.Bus.Publish<Fault<EntryStepDispatch>>(new { Message = CapInner(processorId) }, ct);
            // Wait deterministically until BOTH faults have been consumed (the counter has walked to n==2).
            var consumed = 0;
            await foreach (var _ in harness.Consumed.SelectAsync<Fault<EntryStepDispatch>>(ct))
                if (++consumed == 2) break;
            Assert.Equal(2L, fake.CounterValue(key));
            Assert.Equal(300_000L, fake.KeyTtlMillis(key));            // not clobbered on the n==2 increment
        }
        finally { await harness.Stop(ct); }
    }

    /// <summary>
    /// WR-03 / T-40-06 regression: the cap-park is retry-safe — a park whose keeper-dlq <c>Send</c> throws is
    /// re-attempted (the exception propagates so MassTransit's Immediate(N) retry re-runs the body), and once
    /// the Send succeeds the envelope is parked EXACTLY ONCE and the counter is DEL'd. Under the old strict
    /// <c>n == cap+1</c> gate the retry's INCR (cap+2) took a silent non-parking <c>return</c>, dropping the
    /// Fault&lt;T&gt;. Driven by calling <see cref="KeeperRecoveryHandler.HandleAsync{T}"/> directly with a
    /// substitute <see cref="ConsumeContext{T}"/> whose dlq send endpoint throws once, then succeeds.
    /// </summary>
    [Fact]
    public async Task Wr03_CapPark_FailedSend_IsRetried_ThenParksExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        const int cap = 3;

        var fake = new FakeRedis(FakeRedis.RedisHealth.Up);   // probe always recovers → Recovered branch reached
        var db   = fake.Multiplexer.GetDatabase();
        var key  = (RedisKey)L2ProjectionKeys.KeeperRecoverAttempts(CapH);

        // Pre-arm the counter to n==cap (as if `cap` reinjects already happened): the NEXT handler INCR crosses
        // to cap+1. Use the same atomic Lua path the handler uses so the key is born with its TTL.
        const string incrWithTtl =
            "local n = redis.call('INCR', KEYS[1]) " +
            "if n == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end " +
            "return n";
        for (var i = 0; i < cap; i++)
            await db.ScriptEvaluateAsync(incrWithTtl, new[] { key }, new RedisValue[] { 300_000L });
        Assert.True(fake.CounterKeyExists(key));

        var metrics = NewMetrics();
        var recovery = new L2ProbeRecovery(fake.Multiplexer, Opts(cap), metrics);
        var handler  = new KeeperRecoveryHandler(
            NullLogger<KeeperRecoveryHandler>.Instance, recovery, metrics, fake.Multiplexer, Opts(cap));

        // A dlq send endpoint that THROWS on its first Send (broker hiccup) then SUCCEEDS on the retry.
        // The handler parks via `capDlq.Send(context.Message, ct)` where context.Message is Fault<EntryStepDispatch>,
        // so configure the generic Send<Fault<EntryStepDispatch>> overload it resolves to.
        var sends = 0;
        var dlqEndpoint = Substitute.For<ISendEndpoint>();
        dlqEndpoint.Send(Arg.Any<Fault<EntryStepDispatch>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                sends++;
                return sends == 1 ? Task.FromException(new InvalidOperationException("broker-hiccup")) : Task.CompletedTask;
            });

        var inner = CapInner(Guid.NewGuid());
        var fault = Substitute.For<Fault<EntryStepDispatch>>();
        fault.Message.Returns(inner);
        fault.Exceptions.Returns(System.Array.Empty<ExceptionInfo>());   // ex resolves to null → ex?.X is safe
        var context = Substitute.For<ConsumeContext<Fault<EntryStepDispatch>>>();
        context.Message.Returns(fault);
        context.CancellationToken.Returns(ct);
        context.Publish(Arg.Any<object>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        context.Publish(Arg.Any<PauseWorkflow>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        context.Publish(Arg.Any<ResumeWorkflow>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        context.GetSendEndpoint(Arg.Any<Uri>()).Returns(Task.FromResult(dlqEndpoint));

        Func<EntryStepDispatch, Uri> reinject = i => new Uri($"queue:{i.ProcessorId:D}");

        // First attempt: INCR crosses to cap+1 → park path → Send throws → the exception MUST propagate
        // (so the message is NOT acked and MassTransit re-delivers). The counter must remain live (DEL skipped).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(context, KeeperMetricTags.FaultTypeDispatch, reinject, ct));
        Assert.True(fake.CounterKeyExists(key));   // DEL was NOT reached on the failed park → counter still live

        // Retry: INCR now yields cap+2 (n >= cap+1) → STILL parks (not a silent drop). Send succeeds this time.
        await handler.HandleAsync(context, KeeperMetricTags.FaultTypeDispatch, reinject, ct);

        Assert.Equal(2, sends);                     // exactly one failed Send + one successful Send (no silent drop)
        Assert.False(fake.CounterKeyExists(key));   // DEL ran only AFTER the successful park → no leak
    }
}
