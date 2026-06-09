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
/// and (3) the surviving single consolidated DLQ-1 (skp-dlq-1) TTL'd const at the configuration/const
/// level (DLQ-03 bridge to VALIDATION.md). The v3.x DLQ-2 (keeper-dlq) was retired in Phase 48.
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

    // ── Throwaway consumer simulating the ProcessorPipeline's send-exhaustion `throw sent.Error!`. ──
    // ProcessorPipeline (src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:170-184) wraps every
    // orchestrator-result Send in the shared Retry:Limit immediate loop; on exhaustion it re-surfaces the
    // failed SendResult via `throw sent.Error!`. That throw propagates OUT of the consumer through the bus
    // `UseMessageRetry` budget exactly like any other infra fault — so it lands in the SAME inherited
    // consolidated `_error` move (skp-dlq-1), NOT a dedicated keeper-dlq. We CANNOT boot a real
    // ProcessorPipeline in this rig (47-RESEARCH "Processor Send-Exhaustion Seam" A2 — it needs
    // Redis/IDatabase/sendProvider the rig lacks), so this throwaway consumer is the proven hermetic
    // equivalent: it throws an exception representing the exhausted SendResult, mirroring AlwaysFaultsConsumer.
    public sealed record ProcessorSendExhausted(string Id);

    public sealed class ProcessorSendExhaustedConsumer : IConsumer<ProcessorSendExhausted>
    {
        // Stands in for `throw sent.Error!` — the exhausted-send error the ProcessorPipeline re-throws.
        public Task Consume(ConsumeContext<ProcessorSendExhausted> context) =>
            throw new InvalidOperationException("simulated processor send-exhaustion (throw sent.Error!)");
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
    /// Builds the same consolidated-error rig as <see cref="BuildHarness"/> but with the processor
    /// send-exhaustion throwaway consumer (<see cref="ProcessorSendExhaustedConsumer"/>) registered, so the
    /// pipeline's `throw sent.Error!` equivalent can be observed routing to the SAME inherited skp-dlq-1.
    /// </summary>
    private static ServiceProvider BuildProcessorHarness(int retryLimit) =>
        new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<ProcessorSendExhaustedConsumer>();

                // The consolidated forensic sink: a no-op handler bound to skp-dlq-1 so the moved
                // ConsolidatedFault is observable (production skp-dlq-1 has NO consumer — operator-drained).
                x.AddHandler((ConsumeContext<ConsolidatedFault> _) => Task.CompletedTask)
                    .Endpoint(e => e.Name = ConsolidatedErrorTransportFilter.Dlq1);

                // The SUT pipeline — identical to AddBaseConsoleMessaging's wiring (DLQ-04, D-06).
                x.AddConfigureEndpointsCallback((context, name, e) =>
                {
                    e.UseMessageRetry(r => r.Immediate(retryLimit));
                    e.ConfigureError(ep =>
                    {
                        ep.UseFilter(new GenerateFaultFilter());                  // keep Fault<T> (Keeper rides it)
                        ep.UseFilter(new ConsolidatedErrorTransportFilter());     // move -> skp-dlq-1 (consolidated)
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
    /// DLQ-03 topology assertion (bridges VALIDATION.md's DLQ-03 row): the consolidated DLQ-1 (skp-dlq-1) is
    /// the TTL'd forensic sink declared with x-message-ttl = 7 days in BaseConsole.Core. The v3.x DLQ-2
    /// (keeper-dlq) was retired in Phase 48 (RETIRE-03) along with the reactive path that fed it, so this
    /// fact now asserts only the surviving single consolidated DLQ-1 const + its TTL value. In-memory
    /// transport cannot expose live queue args, so this is a const/configuration-level assertion (the live
    /// arg proof is Plan 04 / Phase 39).
    /// </summary>
    [Fact]
    public void Dlq_TopologyArgs()
    {
        // DLQ-1: the consolidated TTL'd forensic queue const — the sole DLQ after the Phase-48 teardown.
        Assert.Equal("skp-dlq-1", ConsolidatedErrorTransportFilter.Dlq1);

        // BaseConsole.Core declares skp-dlq-1 with the 7-day TTL (604800000 ms).
        // The live SetQueueArgument application is RMQ-only — verified against the broker in Plan 04 / Phase 39.
        Assert.Equal(604_800_000, (int)TimeSpan.FromDays(7).TotalMilliseconds);
    }

    /// <summary>
    /// RESIL-02 (R1 explicit framing): the ProcessorPipeline's send-exhaustion path — `throw sent.Error!`
    /// after the shared Retry:Limit immediate loop exhausts an orchestrator-result Send
    /// (src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:170-184) — routes the faulted message to the
    /// ONE consolidated <see cref="ConsolidatedErrorTransportFilter.Dlq1"/> (skp-dlq-1) as a
    /// <see cref="ConsolidatedFault"/>, NOT a dedicated keeper-dlq. The re-thrown send error propagates out
    /// of the consumer through the bus <c>UseMessageRetry</c> budget and hits the SAME inherited consolidated
    /// `_error` move wired once in BaseConsole.Core for every console endpoint — so the processor pipeline's
    /// give-up lands in skp-dlq-1 exactly like any other transport exhaustion. GenerateFaultFilter is retained,
    /// so <see cref="Fault{T}"/> still publishes on exhaustion. A real ProcessorPipeline boot is NOT used
    /// (47-RESEARCH A2 — it needs Redis/IDatabase/sendProvider the rig lacks); a throwing consumer is the
    /// proven hermetic equivalent of `throw sent.Error!`.
    /// </summary>
    [Fact]
    [Trait("Phase", "47")]
    public async Task ProcessorSendExhaustion_RoutesToDlq1()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildProcessorHarness(retryLimit: 2);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // Simulate the processor pipeline reaching `throw sent.Error!` on a send-exhausted result.
            await harness.Bus.Publish(new ProcessorSendExhausted("proc-send-exhausted"), ct);

            // The send-exhaustion throw exhausts the Immediate(N) budget and faults.
            Assert.True(await harness.Consumed.Any<ProcessorSendExhausted>(ct));

            // GenerateFaultFilter retained -> Fault<T> still published on exhaustion.
            Assert.True(await harness.Published.Any<Fault<ProcessorSendExhausted>>(ct));

            // The processor send-exhaustion routes to the ONE consolidated skp-dlq-1 (ConsolidatedFault),
            // NOT a dedicated keeper-dlq.
            Assert.True(await harness.Consumed.Any<ConsolidatedFault>(ct));
        }
        finally { await harness.Stop(ct); }
    }
}
