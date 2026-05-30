using System.Linq;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// TEST-RMQ-01 (the #1 topology trap): a single published <see cref="StartOrchestration"/> is
/// BROADCAST to two distinct-InstanceId replicas, NOT load-balanced across them.
/// <para>
/// The production fan-out wiring (<c>src/Orchestrator/Program.cs:28-34</c>) gives each replica a
/// temporary/auto-delete <c>orchestrator-{InstanceId}</c> queue bound to the StartOrchestration
/// message-type exchange. Two DISTINCT InstanceIds = two queues on the same exchange = broadcast.
/// </para>
/// <para>
/// A2 resolution (deferred from Plan 20-01): MassTransit keys consumer harnesses by consumer TYPE,
/// so two endpoints hosting the SAME consumer type collapse into one
/// <see cref="ITestHarness.GetConsumerHarness{T}"/> lookup slot. We use TWO distinct thin consumer
/// types (<see cref="FanOutConsumerA"/> / <see cref="FanOutConsumerB"/>) so each replica gets its
/// own independently-assertable slot. This is the unambiguous single-harness/two-types shape; the
/// two-providers fallback was not needed.
/// </para>
/// TEST-RMQ-04: both endpoints are <c>e.Temporary = true</c> with per-test-class-prefixed
/// InstanceIds (<c>t01-fanout-*</c>); in-memory endpoints are inherently transient and no global
/// queue purge is performed in teardown.
/// </summary>
public sealed class FanOutBroadcastTests
{
    // Two distinct consumer types so GetConsumerHarness<T>() resolves a per-replica slot each.
    private sealed class FanOutConsumerA : IConsumer<StartOrchestration>
    {
        public Task Consume(ConsumeContext<StartOrchestration> context) => Task.CompletedTask;
    }

    private sealed class FanOutConsumerB : IConsumer<StartOrchestration>
    {
        public Task Consume(ConsumeContext<StartOrchestration> context) => Task.CompletedTask;
    }

    [Fact]
    public async Task One_Publish_Is_Broadcast_To_Both_Distinct_InstanceId_Endpoints_Count_Is_Two()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<FanOutConsumerA>()
                    .Endpoint(e => { e.InstanceId = "t01-fanout-a"; e.Temporary = true; });
                x.AddConsumer<FanOutConsumerB>()
                    .Endpoint(e => { e.InstanceId = "t01-fanout-b"; e.Temporary = true; });
                x.UsingInMemory((c, cfg) => cfg.ConfigureEndpoints(c));
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(
                new StartOrchestration([Guid.NewGuid()]) { CorrelationId = NewId.NextGuid() }, ct);

            // BROADCAST: BOTH consumer harnesses observed the SAME single message.
            Assert.True(await harness.GetConsumerHarness<FanOutConsumerA>()
                .Consumed.Any<StartOrchestration>(ct));
            Assert.True(await harness.GetConsumerHarness<FanOutConsumerB>()
                .Consumed.Any<StartOrchestration>(ct));

            // ANTI-LOAD-BALANCE (Pitfall 1): total consumed == 2 (one per endpoint), NOT 1.
            // A bare .Any() would be true for load-balance too — count==2 is the discriminating proof.
            // 8.5.5 API NOTE: ITestHarness.Consumed has no Count<T>() (its element type is not
            // IAsyncListElement). The per-message-type count is the LINQ .Count() over the
            // materialized Select<T>(ct) list — the same Select<T>(ct) idiom used by
            // OrchestrationServicePublishTests.cs:144 (.Single()).
            Assert.Equal(2, harness.Consumed.Select<StartOrchestration>(ct).Count());

            // No consume faulted.
            Assert.False(await harness.Consumed.Any<StartOrchestration>(m => m.Exception != null, ct));
        }
        finally { await harness.Stop(ct); }
    }
}
