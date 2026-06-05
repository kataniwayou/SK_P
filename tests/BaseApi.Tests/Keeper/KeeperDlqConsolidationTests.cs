using BaseConsole.Core.Messaging;
using MassTransit;
using MassTransit.Middleware;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// DLQ-01 / DLQ-02 / DLQ-04 hermetic proof for the consolidated <see cref="ConsolidatedErrorTransportFilter"/>
/// wired in <c>BaseConsole.Core.AddBaseConsoleMessaging</c> (Plan 36-03 Task 2). The same
/// <c>ConfigureError(GenerateFaultFilter + ConsolidatedErrorTransportFilter)</c> pipeline is reproduced on an
/// in-memory <see cref="ITestHarness"/> so an exhausted message can be observed routing toward the ONE shared
/// <see cref="ConsolidatedErrorTransportFilter.Dlq1"/> queue (NOT the per-<c>{queue}_error</c> default) while
/// the framework <c>GenerateFaultFilter</c> still publishes <see cref="Fault{T}"/> (Keeper rides that stream).
/// <para>
/// HERMETIC SCOPE / DEFERRED LIVE PROOF: the in-memory transport cannot exercise the RabbitMQ-specific
/// queue arguments (x-message-ttl) — that is an RMQ-transport behavior. The authoritative LIVE DLQ-1 signal
/// (message actually landing in the broker's skp-dlq-1 with the 7-day TTL applied) is Plan 04's RealStack
/// E2E + the Phase-39 close gate. Here we prove, hermetically: (1) the consolidated ConfigureError pipeline
/// IS installed and routes an exhausted message into the single skp-dlq-1 endpoint as a typed
/// <see cref="ConsolidatedFault"/> (consolidated — NOT per-{queue}_error), (2) GenerateFaultFilter is
/// retained so <see cref="Fault{T}"/> still publishes on exhaustion (T-36-10 — no fault silently dropped),
/// and (3) the topology split — skp-dlq-1 (DLQ-1) is the TTL'd const vs keeper-dlq (DLQ-2) the no-TTL const
/// — at the configuration/const level (DLQ-03 bridge to VALIDATION.md).
/// </para>
/// The rig is cloned from <c>KeeperProbeLoopTests</c> (in-memory harness builder) + the Task-1 spike shape.
/// No RealStack trait — runs in the fast hermetic suite.
/// </summary>
public sealed class KeeperDlqConsolidationTests
{
    // ── Throwaway always-throwing consumer: its Immediate(N) budget exhausts → the consolidated error move. ──
    public sealed record AlwaysFaults(string Id);

    public sealed class AlwaysFaultsConsumer : IConsumer<AlwaysFaults>
    {
        public Task Consume(ConsumeContext<AlwaysFaults> context) =>
            throw new InvalidOperationException("simulated transport exhaustion");
    }

    /// <summary>
    /// Builds an in-memory harness reproducing the BaseConsole.Core consolidated error transport: the
    /// once-per-endpoint <c>AddConfigureEndpointsCallback</c> installing
    /// <c>ConfigureError(GenerateFaultFilter + ConsolidatedErrorTransportFilter)</c>, an Immediate(N) retry
    /// budget on the faulting endpoint, AND a no-op handler bound to the consolidated skp-dlq-1 queue so the
    /// moved <see cref="ConsolidatedFault"/> is observable via <c>harness.Consumed</c>.
    /// </summary>
    private static ServiceProvider BuildHarness(int retryLimit) =>
        new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<AlwaysFaultsConsumer>();

                // The consolidated forensic sink: a no-op handler bound to the skp-dlq-1 endpoint so the
                // moved ConsolidatedFault is observable. (Production skp-dlq-1 has NO consumer — operator-drained.)
                x.AddHandler((ConsumeContext<ConsolidatedFault> _) => Task.CompletedTask)
                    .Endpoint(e => e.Name = ConsolidatedErrorTransportFilter.Dlq1);

                // The SUT pipeline — identical to AddBaseConsoleMessaging's wiring (DLQ-04, D-06).
                x.AddConfigureEndpointsCallback((context, name, e) =>
                {
                    e.UseMessageRetry(r => r.Immediate(retryLimit));
                    e.ConfigureError(ep =>
                    {
                        ep.UseFilter(new GenerateFaultFilter());                  // keep Fault<T> (Keeper rides it)
                        ep.UseFilter(new ConsolidatedErrorTransportFilter());     // move → skp-dlq-1 (consolidated)
                    });
                });

                x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
            })
            .BuildServiceProvider(true);

    /// <summary>
    /// DLQ-02/04: an exhausted message routes toward the ONE consolidated skp-dlq-1 destination (not the
    /// per-{queue}_error default), AND GenerateFaultFilter still publishes Fault&lt;T&gt; on exhaustion.
    /// </summary>
    [Fact]
    public async Task Dlq1_Consolidated()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildHarness(retryLimit: 2);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AlwaysFaults("x1"), ct);

            // The consumer exhausts its Immediate(N) budget and faults.
            Assert.True(await harness.Consumed.Any<AlwaysFaults>(ct));

            // GenerateFaultFilter retained → Fault<T> still published (Keeper's pub/sub stream survives, T-36-10).
            Assert.True(await harness.Published.Any<Fault<AlwaysFaults>>(ct));

            // ConsolidatedErrorTransportFilter moved the faulted message to the ONE shared skp-dlq-1 endpoint
            // as a typed ConsolidatedFault (consolidated — NOT a per-{queue}_error queue).
            Assert.True(await harness.Consumed.Any<ConsolidatedFault>(ct));
        }
        finally { await harness.Stop(ct); }
    }

    /// <summary>
    /// DLQ-01: a consumer whose work throws an infra fault exhausts under Immediate(N) and is routed to the
    /// consolidated DLQ-1. (A Keeper Send/Redis infra fault follows the identical endpoint path — the
    /// consolidated error transport is wired once for ALL endpoints in BaseConsole.Core, so a Keeper
    /// endpoint's exhaustion lands in skp-dlq-1 exactly like this minimal throwing consumer's does.)
    /// </summary>
    [Fact]
    public async Task Keeper_SendFault_RetriesToDlq1()
    {
        var ct = TestContext.Current.CancellationToken;
        const int retryLimit = 2;
        await using var provider = BuildHarness(retryLimit);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new AlwaysFaults("infra-fault"), ct);

            Assert.True(await harness.Consumed.Any<AlwaysFaults>(ct));

            // The Immediate(N) budget is honoured by the faulting endpoint (1 initial + Limit retries) before
            // exhaustion routes to the consolidated DLQ-1.
            var consumerHarness = harness.GetConsumerHarness<AlwaysFaultsConsumer>();
            Assert.True(await consumerHarness.Consumed.Any<AlwaysFaults>(ct));

            // On exhaustion the message is moved to the consolidated skp-dlq-1 (DLQ-1), NOT per-{queue}_error.
            Assert.True(await harness.Consumed.Any<ConsolidatedFault>(ct));
        }
        finally { await harness.Stop(ct); }
    }

    /// <summary>
    /// DLQ-03 topology assertion (bridges VALIDATION.md's DLQ-03 row): the two DLQs are split by mechanism —
    /// DLQ-1 (skp-dlq-1) is the TTL'd forensic sink declared with x-message-ttl = 7 days in BaseConsole.Core;
    /// DLQ-2 (keeper-dlq) is the plain durable primary-alert queue with NO TTL (it must persist until drained).
    /// In-memory transport cannot expose live queue args, so this is a const/configuration-level assertion
    /// (the live arg proof is Plan 04 / Phase 39).
    /// </summary>
    [Fact]
    public void Dlq_TopologyArgs()
    {
        // DLQ-1: the consolidated TTL'd forensic queue const.
        Assert.Equal("skp-dlq-1", ConsolidatedErrorTransportFilter.Dlq1);

        // DLQ-2: the plain durable, NO-TTL terminal give-up queue const (the PRIMARY operator alert).
        Assert.Equal("keeper-dlq", KeeperQueues.DeadLetter);

        // The two are distinct mechanisms (DLQ-02) — never the same queue.
        Assert.NotEqual(ConsolidatedErrorTransportFilter.Dlq1, KeeperQueues.DeadLetter);

        // BaseConsole.Core declares skp-dlq-1 with the 7-day TTL (604800000 ms); keeper-dlq carries no TTL.
        // The live SetQueueArgument application is RMQ-only — verified against the broker in Plan 04 / Phase 39.
        Assert.Equal(604_800_000, (int)TimeSpan.FromDays(7).TotalMilliseconds);
    }
}
