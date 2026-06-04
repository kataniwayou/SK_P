using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Wave-0 MassTransit-reality probe (Plan 32-01, Risk R2 / D-06 assumption): proves
/// <c>Fault&lt;EntryStepDispatch&gt;.Message.WorkflowId</c> round-trips through REAL MassTransit fault
/// publication — i.e. that MassTransit actually populates <c>Fault&lt;T&gt;.Message</c> with the original
/// message instance, so the double-<c>.Message</c> extraction the Plan-05 fault consumer relies on
/// (<c>context.Message.Message.WorkflowId</c>) works (D-06 needs NO fallback).
/// <para>
/// This uses a REAL in-memory MassTransit harness (NOT a pure NSubstitute stub — a stub would only prove
/// our own code reads <c>.Message.Message</c>, not that MT populates it). A throwing
/// <see cref="EntryStepDispatch"/> consumer with <c>Immediate(0)</c> exhausts immediately, MassTransit
/// auto-publishes <c>Fault&lt;EntryStepDispatch&gt;</c>, and a probe
/// <c>IConsumer&lt;Fault&lt;EntryStepDispatch&gt;&gt;</c> captures the inner <c>WorkflowId</c>.
/// </para>
/// Hermetic (in-memory harness), NOT RealStack.
/// </summary>
[Trait("Category", "Hermetic")]
public sealed class FaultConsumerBindingFacts
{
    /// <summary>
    /// Throws on every delivery so the <c>Immediate(0)</c> budget exhausts on the first delivery and
    /// MassTransit publishes <c>Fault&lt;EntryStepDispatch&gt;</c>.
    /// </summary>
    private sealed class ThrowingDispatchConsumer : IConsumer<EntryStepDispatch>
    {
        public Task Consume(ConsumeContext<EntryStepDispatch> context)
            => throw new InvalidOperationException("forced infra fault to trigger Fault<EntryStepDispatch> publication");
    }

    /// <summary>
    /// The fault-fanout probe: mirrors the Plan-05 <c>FaultUnscheduleConsumer</c> binding. Captures the
    /// double-<c>.Message</c> extraction <c>context.Message.Message.WorkflowId</c> (outer is
    /// <c>Fault&lt;EntryStepDispatch&gt;</c>, inner is the original <c>EntryStepDispatch</c>).
    /// </summary>
    private sealed class FaultProbeConsumer : IConsumer<Fault<EntryStepDispatch>>
    {
        public static volatile int Invocations;
        public static Guid CapturedWorkflowId;

        public Task Consume(ConsumeContext<Fault<EntryStepDispatch>> context)
        {
            Interlocked.Increment(ref Invocations);
            CapturedWorkflowId = context.Message.Message.WorkflowId;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Fault_Message_WorkflowId_RoundTrips_Through_Real_MassTransit_Fault_Publication()
    {
        var ct = TestContext.Current.CancellationToken;
        FaultProbeConsumer.Invocations = 0;
        FaultProbeConsumer.CapturedWorkflowId = Guid.Empty;

        var knownWorkflowId = Guid.NewGuid();

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<ThrowingDispatchConsumer>();
                x.AddConsumer<FaultProbeConsumer>();
                x.UsingInMemory((ctx, cfg) =>
                {
                    // The throwing dispatch endpoint with a tiny budget — exhausts fast so MT publishes
                    // Fault<EntryStepDispatch>.
                    cfg.ReceiveEndpoint("fault-binding-dispatch", e =>
                    {
                        e.UseMessageRetry(r => r.Immediate(0));
                        e.ConfigureConsumer<ThrowingDispatchConsumer>(ctx);
                    });
                    // The probe fault-consumer endpoint, bound to Fault<EntryStepDispatch>.
                    cfg.ReceiveEndpoint("fault-binding-probe", e =>
                    {
                        e.ConfigureConsumer<FaultProbeConsumer>(ctx);
                    });
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            await harness.Bus.Publish(
                new EntryStepDispatch(knownWorkflowId, NewId.NextGuid(), NewId.NextGuid(), "{}")
                {
                    CorrelationId = NewId.NextGuid(),
                },
                ct);

            // Await the probe consuming the auto-published Fault<EntryStepDispatch>.
            var probe = harness.GetConsumerHarness<FaultProbeConsumer>();
            Assert.True(
                await probe.Consumed.Any<Fault<EntryStepDispatch>>(ct),
                "expected the throwing dispatch endpoint to exhaust and the probe to consume Fault<EntryStepDispatch>");

            // The probe was invoked exactly once...
            Assert.Equal(1, FaultProbeConsumer.Invocations);

            // ...and the captured WorkflowId == the KNOWN fixed Guid (proves Fault.Message IS the original
            // EntryStepDispatch instance carrying WorkflowId — D-06 holds, no fallback needed).
            Assert.NotEqual(Guid.Empty, FaultProbeConsumer.CapturedWorkflowId);
            Assert.Equal(knownWorkflowId, FaultProbeConsumer.CapturedWorkflowId);

            // Belt-and-braces (Risk R2 / A4): no consume on the probe faulted.
            Assert.False(await probe.Consumed.Any<Fault<EntryStepDispatch>>(m => m.Exception != null, ct));
        }
        finally { await harness.Stop(ct); }
    }
}
