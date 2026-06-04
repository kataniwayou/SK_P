using System.Collections.Concurrent;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Wave-0 MassTransit-reality probe (Plan 32-01, Risk R1 / Assumptions A1+A2): pins the EXACT
/// <c>ConsumeContext.GetRetryAttempt()</c> value observed on the exhausting delivery of a SINGLE
/// endpoint-level <c>UseMessageRetry(r =&gt; r.Immediate(LIMIT))</c> policy — the documented reliable
/// case (this project configures one receive-endpoint-level policy; cf.
/// <c>src/Orchestrator/Consumers/ResultConsumerDefinition.cs</c> and
/// <c>ProcessorStartupOrchestrator</c>'s <c>ConnectReceiveEndpoint</c> bind).
/// <para>
/// This drives a REAL in-memory MassTransit bus (NOT the NSubstitute
/// <c>OrchestratorTestStubs.Context</c> — a Substitute <c>ConsumeContext</c> does NOT implement the
/// <c>GetRetryAttempt()</c> header extension, so the value can only be observed against a real bus).
/// </para>
/// <para>
/// The 32-SPEC locks the breaker trigger at <c>GetRetryAttempt() == Limit</c>. This test PROVES that
/// <c>Limit</c> is the correct boundary value for this config (0-based: the first delivery records
/// <c>0</c>, the LIMIT-th retry — i.e. the exhausting delivery — records <c>LIMIT</c>, so the observed
/// attempts are <c>0,1,2,3</c> for <c>LIMIT=3</c> and the delivery that should trip the breaker is
/// <c>== LIMIT</c>). The total number of deliveries is <c>LIMIT + 1</c>.
/// </para>
/// <para>
/// ESCALATION (Risk R1, MassTransit#1217/#3216): if the pinned boundary on the exhausting delivery is
/// NOT <c>== LIMIT</c> (e.g. it returns <c>0</c> every delivery, the consumer-level/stacked-policy bug),
/// this test asserts the ACTUAL observed boundary, carries a <c>[Trait("Escalate","Risk-R1")]</c>
/// trait, and the plan SUMMARY records a BLOCKING escalation note for Plan 04. Pinning the truth IS the
/// pass condition — the SPEC's <c>== Limit</c> is never silently changed. Hermetic (in-memory harness),
/// NOT RealStack.
/// </para>
/// </summary>
[Trait("Category", "Hermetic")]
public sealed class RetryAttemptNumberingFacts
{
    /// <summary>
    /// The single endpoint-level retry budget under test. Mirrors the production
    /// <c>Immediate(retryOptions.Value.Limit)</c> idiom (default <c>RetryOptions.Limit = 3</c>).
    /// </summary>
    private const int LIMIT = 3;

    /// <summary>A trivial message type for the probe — the type does not matter, only the retry behavior.</summary>
    public sealed record RetryProbeMessage(Guid Id);

    /// <summary>
    /// Records <c>GetRetryAttempt()</c> for every delivery (thread-safe, ordered by arrival), then
    /// UNCONDITIONALLY throws an infra-style exception so the single <c>Immediate(LIMIT)</c> policy
    /// exhausts and the message reaches <c>_error</c>.
    /// </summary>
    private sealed class RetryProbeConsumer : IConsumer<RetryProbeMessage>
    {
        // Static so the assertion can read the recorded attempts after the harness drives the deliveries.
        // Cleared at the start of each test run (single test in this class).
        public static readonly ConcurrentQueue<int> Attempts = new();

        public Task Consume(ConsumeContext<RetryProbeMessage> context)
        {
            Attempts.Enqueue(context.GetRetryAttempt());
            // Infra-style throw — the exception TYPE is irrelevant; only that it forces retry exhaustion.
            throw new InvalidOperationException("forced infra fault to exhaust the Immediate(LIMIT) retry budget");
        }
    }

    [Fact]
    public async Task GetRetryAttempt_On_Exhausting_Delivery_Of_Single_Immediate_Limit_Policy_Equals_Limit()
    {
        var ct = TestContext.Current.CancellationToken;
        while (RetryProbeConsumer.Attempts.TryDequeue(out _)) { }   // hermetic: start from empty

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<RetryProbeConsumer>();
                x.UsingInMemory((ctx, cfg) =>
                {
                    // ONE receive endpoint with a SINGLE endpoint-level Immediate(LIMIT) policy — the
                    // documented reliable case (mirrors ResultConsumerDefinition's endpoint-level wiring).
                    cfg.ReceiveEndpoint("retry-attempt-probe", e =>
                    {
                        e.UseMessageRetry(r => r.Immediate(LIMIT));
                        e.ConfigureConsumer<RetryProbeConsumer>(ctx);
                    });
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(new RetryProbeMessage(Guid.NewGuid()), ct);

            // Await the exhaustion: MassTransit publishes Fault<RetryProbeMessage> AND dead-letters to
            // _error from the SAME mechanism once the Immediate(LIMIT) budget is spent.
            Assert.True(
                await harness.Published.Any<Fault<RetryProbeMessage>>(ct),
                "expected the Immediate(LIMIT) policy to exhaust and MassTransit to publish Fault<RetryProbeMessage>");

            var observed = RetryProbeConsumer.Attempts.ToArray();

            // The consumer body re-executes on every (re)delivery, so the count is LIMIT + 1 deliveries:
            // the initial delivery (attempt 0) + LIMIT retries.
            Assert.Equal(LIMIT + 1, observed.Length);

            // PIN the boundary. SPEC locks `== LIMIT` on the exhausting delivery.
            // 0-based: first delivery records 0; the i-th retry records i; the LIMIT-th (final/exhausting)
            // retry records LIMIT. So the full observed sequence is 0,1,2,...,LIMIT.
            Assert.Equal(Enumerable.Range(0, LIMIT + 1).ToArray(), observed);

            // The first delivery is attempt 0.
            Assert.Equal(0, observed[0]);

            // The value on the FINAL (exhausting) delivery — the one that should trip the breaker — is == LIMIT.
            // This is the load-bearing assertion the breaker seam (Plan 04) reads.
            Assert.Equal(LIMIT, observed[^1]);
        }
        finally { await harness.Stop(ct); }
    }
}
