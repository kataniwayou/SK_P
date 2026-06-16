using global::Keeper.Observability;
using global::Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 70 / KINJ-03 — the cross-consumer delete invariant: <b>DELETE is the only keeper recovery state
/// that deletes keys</b>. Behavioral (D-04): each fact constructs the REAL consumer over a
/// <see cref="RecoveryTestKit"/> substitute and asserts on the <c>KeyDeleteAsync</c> calls it actually made.
/// <list type="bullet">
///   <item><description><c>DeleteConsumer</c> DOES delete — ONE atomic multi-key (<c>RedisKey[]</c>) DEL of
///   BOTH the data key and the allocation index (the positive half).</description></item>
///   <item><description><c>InjectConsumer</c> does NOT delete — <c>DidNotReceive</c> on BOTH overloads,
///   co-asserted with the captured <see cref="StepCompleted"/> send so a silent no-op cannot pass.</description></item>
///   <item><description><c>ReinjectConsumer</c> does NOT delete — same negative guard, co-asserted with the
///   captured <see cref="EntryStepDispatch"/> send (STRLEN stubbed present so its send path runs).</description></item>
/// </list>
/// CARVE-OUT (Pitfall 3): this fact NEVER instantiates <c>L2ProbeRecovery</c> and never enumerates
/// <c>Keeper.dll</c> types — its <c>:35</c> scratch self-delete is structurally outside the invariant.
/// </summary>
[Trait("Phase", "70")]
public sealed class KeeperDeleteInvariantFacts
{
    [Fact]
    public async Task DeleteConsumer_deletes_both_keys()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new DeleteConsumer(
            RecoveryTestKit.Mux(db), send, RecoveryTestKit.Retry());

        var m = new KeeperDelete(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
        };
        var ctx = Substitute.For<ConsumeContext<KeeperDelete>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);

        await consumer.Consume(ctx);

        // POSITIVE half: DELETE issues ONE atomic both-key DEL (data key + allocation index).
        await db.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey[]>(ks => ks.Length == 2
                && ks.Contains((RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId))
                && ks.Contains((RedisKey)L2ProjectionKeys.MessageIndex(m.MessageId))),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task InjectConsumer_never_deletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();

        var consumer = new InjectConsumer(
            RecoveryTestKit.Mux(db), send, RecoveryTestKit.Retry());

        var m = new KeeperInject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            Data = "{\"out\":1}",
        };
        var ctx = Substitute.For<ConsumeContext<KeeperInject>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);

        await consumer.Consume(ctx);

        // POSITIVE co-assertion: the body provably ran to completion (a StepCompleted was sent) — so the
        // DidNotReceive below cannot be satisfied by a silent no-op.
        var (_, msg) = Assert.Single(send.Sent);
        Assert.IsType<StepCompleted>(msg);

        // NEGATIVE guard: INJECT deletes nothing — BOTH overloads (single-key AND multi-key, Pitfall 2).
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(),   Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task ReinjectConsumer_never_deletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = RecoveryTestKit.Db();
        var send = new RecoveryTestKit.CapturingSendProvider();

        var m = new KeeperReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            Payload = "{\"cfg\":7}",
        };
        // ReinjectConsumer drops on STRLEN==0; stub present so its send path runs (else it no-ops and the
        // positive co-assertion below would fail).
        db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId), Arg.Any<CommandFlags>())
            .Returns(10L);   // present

        var consumer = new ReinjectConsumer(
            RecoveryTestKit.Mux(db), send, RecoveryTestKit.Retry(),
            RecoveryTestKit.Metrics(), NullLogger<ReinjectConsumer>.Instance);

        var ctx = Substitute.For<ConsumeContext<KeeperReinject>>();
        ctx.Message.Returns(m);
        ctx.CancellationToken.Returns(ct);

        await consumer.Consume(ctx);

        // POSITIVE co-assertion: the body provably ran (one EntryStepDispatch re-injected) — so DidNotReceive
        // cannot pass on a silent no-op.
        var (_, msg) = Assert.Single(send.Sent);
        Assert.IsType<EntryStepDispatch>(msg);

        // NEGATIVE guard: REINJECT deletes nothing — BOTH overloads (Pitfall 2).
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(),   Arg.Any<CommandFlags>());
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
    }
}
