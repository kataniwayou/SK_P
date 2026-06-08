using global::Keeper;
using global::Keeper.Health;
using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 46 / KEEP-09 / D-03 (LOCKED Pattern A): the recovery consumer base awaits
/// <see cref="IL2HealthGate.WaitForOpenAsync"/> ONCE at Consume entry, blocking the per-state body until
/// the gate opens; on bound (<c>RecoveryOptions.GateWaitSeconds</c> linked-CTS) exhaustion it throws the
/// TRANSIENT <see cref="RecoveryGateTimeoutException"/> (NOT the terminal <see cref="RecoveryDataGoneException"/>).
/// </summary>
public sealed class RecoveryGateWaitFacts
{
    /// <summary>A gate whose WaitForOpenAsync blocks on a TaskCompletionSource until <see cref="Release"/>;
    /// or never completes (if never released) until the linked CTS cancels it.</summary>
    private sealed class BlockingGate : IL2HealthGate
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Open() => _tcs.TrySetResult();
        public void Close() { }
        public void Release() => _tcs.TrySetResult();
        public async Task WaitForOpenAsync(CancellationToken ct)
        {
            using var reg = ct.Register(() => _tcs.TrySetCanceled(ct));
            await _tcs.Task;
        }
    }

    /// <summary>Trivial subclass exposing whether the body ran (and capturing the message it saw).</summary>
    private sealed class ProbeConsumer(
        IConnectionMultiplexer redis, ISendEndpointProvider send, IL2HealthGate gate,
        IOptions<RetryOptions> retry, IOptions<RecoveryOptions> recovery, IOptions<BackupOptions> backup)
        : RecoveryConsumerBase<KeeperCleanup>(redis, send, gate, retry, recovery, backup)
    {
        public bool BodyRan { get; private set; }
        protected override Task HandleAsync(KeeperCleanup m, CancellationToken ct)
        {
            BodyRan = true;
            return Task.CompletedTask;
        }
    }

    private static ProbeConsumer Build(IL2HealthGate gate, int gateWaitSeconds, out ConsumeContext<KeeperCleanup> ctx)
    {
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(Substitute.For<IDatabase>());
        var send = Substitute.For<ISendEndpointProvider>();
        var consumer = new ProbeConsumer(
            redis, send, gate,
            Options.Create(new RetryOptions { Limit = 3 }),
            Options.Create(new RecoveryOptions { GateWaitSeconds = gateWaitSeconds }),
            Options.Create(new BackupOptions { TtlDays = 2 }));

        ctx = Substitute.For<ConsumeContext<KeeperCleanup>>();
        ctx.Message.Returns(new KeeperCleanup(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
        });
        ctx.CancellationToken.Returns(CancellationToken.None);
        return consumer;
    }

    [Fact]
    [Trait("Phase", "46")]
    public async Task GateWait_blocks_until_gate_opens()
    {
        var gate = new BlockingGate();
        var consumer = Build(gate, gateWaitSeconds: 300, out var ctx);

        var consume = consumer.Consume(ctx);

        // The body must NOT run while the gate is still closed.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(consumer.BodyRan);
        Assert.False(consume.IsCompleted);

        // Release the gate → the body runs and Consume completes.
        gate.Release();
        await consume;
        Assert.True(consumer.BodyRan);
    }

    [Fact]
    [Trait("Phase", "46")]
    public async Task GateWait_bound_exhaustion_throws_transient()
    {
        var gate = new BlockingGate();   // never released → the linked CTS bound fires
        var consumer = Build(gate, gateWaitSeconds: 1, out var ctx);

        await Assert.ThrowsAsync<RecoveryGateTimeoutException>(() => consumer.Consume(ctx));
        Assert.False(consumer.BodyRan);   // body never ran — the gate never opened
    }
}
