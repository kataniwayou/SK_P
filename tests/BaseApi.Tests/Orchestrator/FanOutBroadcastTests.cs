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
            // The per-consumer .Any<T>(ct) AWAITS each harness's consume to settle (the bus-level
            // harness.Consumed list is NOT awaited per-endpoint and races the second delivery).
            var consumerA = harness.GetConsumerHarness<FanOutConsumerA>();
            var consumerB = harness.GetConsumerHarness<FanOutConsumerB>();
            Assert.True(await consumerA.Consumed.Any<StartOrchestration>(ct));
            Assert.True(await consumerB.Consumed.Any<StartOrchestration>(ct));

            // ANTI-LOAD-BALANCE (Pitfall 1): the TOTAL consume operations across the two distinct-
            // InstanceId endpoints == 2 (one delivery per endpoint), NOT 1.
            // 8.5.5 API NOTES (both empirically confirmed this run):
            //  - ITestHarness.Consumed has no Count<T>() (element type is not IAsyncListElement),
            //    so per-message-type counting uses LINQ .Count() over Select<T>(ct) — the same
            //    Select<T>(ct) idiom as OrchestrationServicePublishTests.cs:144 (.Single()).
            //  - Counting the BUS-level harness.Consumed list is FLAKY: it is settled-after-await
            //    only via the per-consumer .Any() above, and reading its raw count races the second
            //    endpoint's delivery (observed Actual:1 intermittently). The robust broadcast proof
            //    is the SUM of the two PER-CONSUMER harness consumed lists, each already awaited to
            //    settlement: A==1 + B==1 == 2. A competing-consumer (load-balance) regression would
            //    give 1 + 0 == 1, so the assertion still discriminates broadcast from load-balance.
            var countA = consumerA.Consumed.Select<StartOrchestration>(ct).Count();
            var countB = consumerB.Consumed.Select<StartOrchestration>(ct).Count();
            Assert.Equal(1, countA);
            Assert.Equal(1, countB);
            Assert.Equal(2, countA + countB);

            // No consume faulted (per-consumer lists, already awaited above).
            Assert.False(await consumerA.Consumed.Any<StartOrchestration>(m => m.Exception != null, ct));
            Assert.False(await consumerB.Consumed.Any<StartOrchestration>(m => m.Exception != null, ct));
        }
        finally { await harness.Stop(ct); }
    }
}
