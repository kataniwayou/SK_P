using System.Linq;
using Keeper.Consumers;
using Keeper.Recovery;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// KEEP-02 binding-shape proof (the INVERSE of <c>FanOutBroadcastTests</c>). FanOut proves broadcast
/// (two distinct-InstanceId endpoints hosting the same message-type → count==2). Round-robin proves
/// load-balance: ONE shared competing-consumer endpoint + ONE consumer type → a single publish is
/// consumed EXACTLY ONCE, not broadcast.
/// <para>
/// <b>Honest scope (RESEARCH A2):</b> the in-memory harness has a single endpoint instance, so this
/// proves the binding SHAPE (shared/durable EndpointName, NOT per-replica auto-delete fan-out), not
/// true cross-replica distribution. The real KEEP-02 cross-replica split is the live-stack operator
/// smoke (Task 3 runbook: <c>docker compose up</c>, publish N, observe the log split across
/// keeper-1/keeper-2). The load-bearing assertion here is <c>count == 1</c>.
/// </para>
/// <para>
/// <c>RetryOptions</c> is registered so <see cref="FaultEntryStepDispatchConsumerDefinition"/>'s
/// <c>IOptions&lt;RetryOptions&gt;</c> ctor resolves (it reads <c>.Value.Limit</c> for
/// <c>UseMessageRetry(Immediate(Limit))</c>).
/// </para>
/// </summary>
public sealed class KeeperRoundRobinTests
{
    [Fact]
    public async Task One_Publish_Is_Delivered_To_Exactly_One_Consumer_On_Shared_Endpoint()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                // Plain AddConsumer + the stable EndpointName from FaultEntryStepDispatchConsumerDefinition =>
                // ONE shared durable queue (NO InstanceId/Temporary per-replica override). This is the
                // competing-consumer shape (D-02): a single publish lands on the one shared endpoint
                // and is consumed once.
                x.AddConsumer<FaultEntryStepDispatchConsumer, FaultEntryStepDispatchConsumerDefinition>();
                x.UsingInMemory((c, cfg) => cfg.ConfigureEndpoints(c));
            })
            // RetryOptions must be bindable so the definition's IOptions<RetryOptions> ctor resolves.
            .Configure<RetryOptions>(o => o.Limit = 3)
            // Phase-36: the consumer now ctor-injects L2ProbeRecovery (→ IConnectionMultiplexer + ProbeOptions).
            // A healthy FakeRedis lets the post-consume probe recover instantly; this test only asserts the
            // round-robin consumed-count==1 binding shape.
            .AddSingleton<IConnectionMultiplexer>(new BaseApi.Tests.Keeper.FakeRedis(FakeRedis.RedisHealth.Up).Multiplexer)
            .Configure<global::Keeper.ProbeOptions>(o => { o.DelaySeconds = 0; o.MaxAttempts = 3; })
            .AddSingleton<L2ProbeRecovery>()
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var inner = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "payload")
            {
                CorrelationId = NewId.NextGuid(),
            };
            // Publish via message initializer — the framework materializes a real Fault<EntryStepDispatch>.
            await harness.Bus.Publish<Fault<EntryStepDispatch>>(new { Message = inner }, ct);

            var consumer = harness.GetConsumerHarness<FaultEntryStepDispatchConsumer>();
            Assert.True(await consumer.Consumed.Any<Fault<EntryStepDispatch>>(ct));

            // LOAD-BALANCE: exactly one delivery on the single shared endpoint (NOT broadcast).
            // Same Select<T>(ct).Count() idiom as FanOutBroadcastTests, materialized to an int local
            // first (the FanOut analog's countA/countB pattern) so the xUnit2013 "use Assert.Single"
            // analyzer does not fire on a collection-size Assert.Equal under TreatWarningsAsErrors.
            // A fan-out regression (per-replica .Endpoint(InstanceId/Temporary)) is discriminated at
            // scale by the live smoke + the durable-queue shape.
            var consumedCount = consumer.Consumed.Select<Fault<EntryStepDispatch>>(ct).Count();
            Assert.Equal(1, consumedCount);

            // No consume faulted.
            Assert.False(await consumer.Consumed.Any<Fault<EntryStepDispatch>>(m => m.Exception != null, ct));
        }
        finally { await harness.Stop(ct); }
    }
}
