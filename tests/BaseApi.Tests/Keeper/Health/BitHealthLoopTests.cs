using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Keeper;
using Keeper.Health;
using Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper.Health;

// KEEP-01/02 (45-01) — the BitHealthLoop edge-triggered BIT probe BackgroundService.
// KEEP-01 probe resilience:
//   - a RedisException reports unhealthy but the loop SURVIVES (next tick still runs)
//   - a non-Redis throw PROPAGATES (is NOT swallowed)
//   - the stoppingToken ends the loop cleanly
// KEEP-02 edge-triggered global broadcast:
//   - healthy->unhealthy publishes PauseAll exactly ONCE
//   - unhealthy->healthy publishes ResumeAll exactly ONCE
//   - same-state ticks publish NOTHING (healthy,healthy,unhealthy,unhealthy,healthy -> 1 Pause + 2 Resume:
//     the leading-healthy run is one prev=null transition Resume; the final healthy is the only other edge)
public sealed class BitHealthLoopTests
{
    private static IOptions<ProbeOptions> ZeroDelay() =>
        Options.Create(new ProbeOptions { DelaySeconds = 0, MaxAttempts = 1 });

    private static RedisConnectionException RedisDown() =>
        new(ConnectionFailureType.UnableToConnect, "fake-down");

    /// <summary>
    /// A scripted <see cref="IConnectionMultiplexer"/>/<see cref="IDatabase"/> double whose probe outcome is
    /// driven by a per-call sequence. Each entry is the exception the READ throws: <c>null</c> = healthy
    /// (read+write succeed); a <see cref="RedisException"/> = unhealthy; any other exception = a genuine bug
    /// that must PROPAGATE.
    /// <para>
    /// Determinism: each scripted tick is fully processed before the next read. When the script is exhausted
    /// the next READ signals <see cref="Exhausted"/> and then PARKS (awaits the stop token) so NO phantom
    /// tick runs. The test awaits <see cref="Exhausted"/> then calls <c>StopAsync</c>, which cancels the
    /// stopping token and swallows the resulting cancellation — a clean graceful shutdown.
    /// </para>
    /// </summary>
    private sealed class ScriptedRedis : IDisposable
    {
        private readonly Queue<Exception?> _script;
        private readonly CancellationTokenSource _park = new();   // releases the exhausted-read park
        private readonly TaskCompletionSource _exhausted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IConnectionMultiplexer Multiplexer { get; }
        public Task Exhausted => _exhausted.Task;

        public ScriptedRedis(IEnumerable<Exception?> script)
        {
            _script = new Queue<Exception?>(script);
            var db = Substitute.For<IDatabase>();
            db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(_ => NextRead());
            db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                    Arg.Any<When>(), Arg.Any<CommandFlags>())
                .Returns(_ => Task.FromResult(true));
            db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(_ => Task.FromResult(true));
            var mux = Substitute.For<IConnectionMultiplexer>();
            mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
            Multiplexer = mux;
        }

        /// <summary>Release the parked exhausted-read so the loop unwinds; call before StopAsync.</summary>
        public void ReleasePark() => _park.Cancel();

        public void Dispose() => _park.Dispose();

        private async Task<RedisValue> NextRead()
        {
            if (_script.Count == 0)
            {
                _exhausted.TrySetResult();                        // every scripted tick consumed
                await Task.Delay(Timeout.Infinite, _park.Token);  // PARK until ReleasePark() -> OCE
                return RedisValue.Null;                            // unreachable
            }
            var ex = _script.Dequeue();
            if (ex is not null)
                throw ex;                                         // RedisException -> unhealthy; other -> propagates
            return RedisValue.Null;
        }
    }

    private static BitHealthLoop NewLoop(L2ProbeRecovery probe, IL2HealthGate gate, IBus bus, RecoveryEndpointHandle holder) =>
        new(probe, gate, bus, holder, ZeroDelay(), NullLogger<BitHealthLoop>.Instance);

    /// <summary>
    /// KEEP-04 (D-04): a populated <see cref="RecoveryEndpointHandle"/> over a substituted
    /// <see cref="IReceiveEndpoint"/> so the BIT loop's edge-driven endpoint Stop/Start calls are assertable.
    /// In 8.5.5 <c>IReceiveEndpoint.Stop(ct)</c> returns a <see cref="Task"/> while <c>Start(ct)</c> returns a
    /// <see cref="ReceiveEndpointHandle"/> (the loop awaits its <c>.Ready</c>) — both interfaces, NSubstitute-able.
    /// Returns the holder (injected into the loop) and the endpoint (the assertion surface for Stop/Start counts).
    /// </summary>
    private static (RecoveryEndpointHandle holder, IReceiveEndpoint endpoint) FakeHandle()
    {
        var started = Substitute.For<ReceiveEndpointHandle>();
        started.Ready.Returns(Task.FromResult(Substitute.For<ReceiveEndpointReady>()));

        var endpoint = Substitute.For<IReceiveEndpoint>();
        endpoint.Start(Arg.Any<CancellationToken>()).Returns(started);
        endpoint.Stop(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var host = Substitute.For<HostReceiveEndpointHandle>();
        host.ReceiveEndpoint.Returns(endpoint);

        return (new RecoveryEndpointHandle { Handle = host }, endpoint);
    }

    // Start the loop, let it consume the whole script, release the park, then graceful-stop.
    private static async Task RunScriptThenStop(BitHealthLoop loop, ScriptedRedis redis, CancellationToken ct)
    {
        await loop.StartAsync(ct);
        await redis.Exhausted.WaitAsync(TimeSpan.FromSeconds(10), ct);
        redis.ReleasePark();
        await loop.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Probe_RedisException_Reports_Unhealthy_Loop_Survives()
    {
        var ct = TestContext.Current.CancellationToken;
        // unhealthy, then healthy: if the loop did NOT survive the RedisException, the second (healthy)
        // tick — and thus its ResumeAll — would never happen.
        using var redis = new ScriptedRedis([RedisDown(), null]);
        var probe = new L2ProbeRecovery(redis.Multiplexer);
        var bus = Substitute.For<IBus>();
        using var loop = NewLoop(probe, new L2HealthGate(), bus, FakeHandle().holder);

        await RunScriptThenStop(loop, redis, ct);

        // Survived the RedisException and went on to the healthy tick.
        await bus.Received(1).Publish(Arg.Any<PauseAll>(), Arg.Any<CancellationToken>());
        await bus.Received(1).Publish(Arg.Any<ResumeAll>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Probe_NonRedis_Throw_Propagates_Not_Swallowed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var redis = new ScriptedRedis([new InvalidOperationException("genuine bug")]);
        var probe = new L2ProbeRecovery(redis.Multiplexer);
        var bus = Substitute.For<IBus>();
        using var loop = NewLoop(probe, new L2HealthGate(), bus, FakeHandle().holder);

        // The non-Redis exception faults ExecuteAsync — it is NOT relabeled "L2 down". Because ExecuteAsync
        // faults at the very first probe (before yielding), BackgroundService.StartAsync surfaces the faulted
        // execute task directly; otherwise it surfaces via ExecuteTask. Drain both so the fault is observed.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await loop.StartAsync(ct);
            await loop.ExecuteTask!;
        });
    }

    [Fact]
    public async Task StoppingToken_Ends_Loop_Cleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        // A steady-healthy script; after it is consumed StopAsync cancels and the loop returns cleanly.
        using var redis = new ScriptedRedis([null, null, null]);
        var probe = new L2ProbeRecovery(redis.Multiplexer);
        var bus = Substitute.For<IBus>();
        using var loop = NewLoop(probe, new L2HealthGate(), bus, FakeHandle().holder);

        await loop.StartAsync(ct);
        await redis.Exhausted.WaitAsync(TimeSpan.FromSeconds(10), ct);
        redis.ReleasePark();

        // Graceful shutdown: StopAsync completes without throwing and the execute task ends.
        await loop.StopAsync(CancellationToken.None);
        Assert.True(loop.ExecuteTask is null || loop.ExecuteTask.IsCompleted);
    }

    [Fact]
    public async Task Edge_Trigger_Publishes_PauseAll_Once_On_Healthy_To_Unhealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        // healthy, healthy, unhealthy: exactly one healthy->unhealthy edge -> one PauseAll.
        using var redis = new ScriptedRedis([null, null, RedisDown()]);
        var probe = new L2ProbeRecovery(redis.Multiplexer);
        var bus = Substitute.For<IBus>();
        using var loop = NewLoop(probe, new L2HealthGate(), bus, FakeHandle().holder);

        await RunScriptThenStop(loop, redis, ct);

        // First healthy tick is a transition (prev=null) -> 1 ResumeAll; the healthy->unhealthy edge -> 1 PauseAll.
        await bus.Received(1).Publish(Arg.Any<PauseAll>(), Arg.Any<CancellationToken>());
        await bus.Received(1).Publish(Arg.Any<ResumeAll>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edge_Trigger_Publishes_ResumeAll_Once_On_Unhealthy_To_Healthy()
    {
        var ct = TestContext.Current.CancellationToken;
        // unhealthy, unhealthy, healthy: one unhealthy->healthy edge -> one ResumeAll (and the first
        // unhealthy tick is a transition -> one PauseAll).
        using var redis = new ScriptedRedis([RedisDown(), RedisDown(), null]);
        var probe = new L2ProbeRecovery(redis.Multiplexer);
        var bus = Substitute.For<IBus>();
        using var loop = NewLoop(probe, new L2HealthGate(), bus, FakeHandle().holder);

        await RunScriptThenStop(loop, redis, ct);

        await bus.Received(1).Publish(Arg.Any<ResumeAll>(), Arg.Any<CancellationToken>());
        await bus.Received(1).Publish(Arg.Any<PauseAll>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Same_State_Ticks_Publish_Nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        // healthy,healthy,unhealthy,unhealthy,healthy. Edge-trigger: same-state ticks publish NOTHING.
        // Transitions: (null->healthy) Resume #1, (healthy->unhealthy) Pause #1, (unhealthy->healthy) Resume #2.
        // => exactly 1 PauseAll + 2 ResumeAll. A per-tick (non-edge) regression would give 3 Pause + 2 Resume.
        using var redis = new ScriptedRedis([null, null, RedisDown(), RedisDown(), null]);
        var probe = new L2ProbeRecovery(redis.Multiplexer);
        var bus = Substitute.For<IBus>();
        using var loop = NewLoop(probe, new L2HealthGate(), bus, FakeHandle().holder);

        await RunScriptThenStop(loop, redis, ct);

        await bus.Received(1).Publish(Arg.Any<PauseAll>(), Arg.Any<CancellationToken>());
        await bus.Received(2).Publish(Arg.Any<ResumeAll>(), Arg.Any<CancellationToken>());
    }

    // KEEP-04 (D-04): the BIT loop drives the keeper-recovery endpoint Stop (unhealthy edge) / Start (healthy
    // edge) on the SAME edges as gate.Close/Open + PauseAll/ResumeAll. These facts mirror the Publish-count
    // facts above with endpoint Stop/Start call counts, proving the non-destructive gate-closed pause + drain.
    [Fact]
    [Trait("Phase", "52")]
    public async Task Healthy_To_Unhealthy_Edge_Stops_Recovery_Endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        // healthy, healthy, unhealthy: the first healthy tick (prev=null) STARTS the endpoint (mirrors the
        // "first healthy tick -> 1 ResumeAll" semantics); the healthy->unhealthy edge STOPS it exactly once.
        using var redis = new ScriptedRedis([null, null, RedisDown()]);
        var probe = new L2ProbeRecovery(redis.Multiplexer);
        var bus = Substitute.For<IBus>();
        var (holder, endpoint) = FakeHandle();
        using var loop = NewLoop(probe, new L2HealthGate(), bus, holder);

        await RunScriptThenStop(loop, redis, ct);

        await endpoint.Received(1).Stop(Arg.Any<CancellationToken>());   // unhealthy edge -> pause (accumulate)
        endpoint.Received(1).Start(Arg.Any<CancellationToken>());        // first healthy transition -> drain
    }

    [Fact]
    [Trait("Phase", "52")]
    public async Task Same_State_Ticks_No_Stop_Start()
    {
        var ct = TestContext.Current.CancellationToken;
        // healthy,healthy,unhealthy,unhealthy,healthy (mirrors Same_State_Ticks_Publish_Nothing). Edge-trigger:
        // same-state ticks issue NO Stop/Start. Transitions: (null->healthy) Start#1, (healthy->unhealthy)
        // Stop#1, (unhealthy->healthy) Start#2 => exactly 1 Stop + 2 Start. A per-tick regression would give
        // 2 Stop + 3 Start.
        using var redis = new ScriptedRedis([null, null, RedisDown(), RedisDown(), null]);
        var probe = new L2ProbeRecovery(redis.Multiplexer);
        var bus = Substitute.For<IBus>();
        var (holder, endpoint) = FakeHandle();
        using var loop = NewLoop(probe, new L2HealthGate(), bus, holder);

        await RunScriptThenStop(loop, redis, ct);

        await endpoint.Received(1).Stop(Arg.Any<CancellationToken>());   // one healthy->unhealthy edge
        endpoint.Received(2).Start(Arg.Any<CancellationToken>());        // first-tick Start + unhealthy->healthy
    }
}
