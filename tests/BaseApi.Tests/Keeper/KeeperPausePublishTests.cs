using System;
using System.Threading;
using System.Threading.Tasks;
using Keeper;
using Keeper.Consumers;
using Keeper.Recovery;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// PAUSE-01 Keeper publish-site proof (Wave 0 RED). Asserts the fan-out behavior the Keeper fault
/// consumer must exhibit once Plan 04 wires the pause/resume publish sites:
/// <list type="bullet">
///   <item><description>At intake the consumer <c>context.Publish</c>es a <c>PauseWorkflow</c> for the
///   inner workflow (so the orchestrator pauses the workflow's Quartz job while the L2 outage is probed).</description></item>
///   <item><description>On <see cref="ProbeOutcome.Recovered"/> it <c>context.Publish</c>es a
///   <c>ResumeWorkflow</c> for the same workflow (D-09 resume-on-recovery).</description></item>
///   <item><description>On <see cref="ProbeOutcome.GaveUp"/> it publishes the <c>PauseWorkflow</c> but
///   NOT a <c>ResumeWorkflow</c> (D-09 give-up → DLQ, no re-pin / no resume).</description></item>
/// </list>
/// Drives <see cref="FaultEntryStepDispatchConsumer.Consume"/> over a substituted
/// <see cref="ConsumeContext{T}"/> (mirroring OrchestratorTestStubs.Context&lt;T&gt;), backing the
/// non-substitutable (sealed, non-virtual) <see cref="L2ProbeRecovery"/> with the shared
/// <see cref="FakeRedis"/> double: an Up Redis → Recovered, a Down Redis → GaveUp. The published
/// <c>PauseWorkflow.WorkflowId</c>/<c>.H</c> must equal the inner message's.
/// <para>
/// RED state: references <c>PauseWorkflow</c>/<c>ResumeWorkflow</c> (Plan 02) AND the publish sites
/// (Plan 04) — fails to compile/assert ONLY because those production symbols/behaviors are absent.
/// </para>
/// </summary>
public sealed class KeeperPausePublishTests
{
    private static IOptions<ProbeOptions> Opts(int maxAttempts = 1, int delaySeconds = 0) =>
        Options.Create(new ProbeOptions { DelaySeconds = delaySeconds, MaxAttempts = maxAttempts });

    private static EntryStepDispatch SampleInner() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "payload")
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid().ToString("D"),
            H = "abc123",
        };

    /// <summary>A ConsumeContext substitute carrying the Fault&lt;T&gt; envelope + a cancellation token.</summary>
    private static ConsumeContext<Fault<EntryStepDispatch>> FaultContext(EntryStepDispatch inner, CancellationToken ct)
    {
        var fault = Substitute.For<Fault<EntryStepDispatch>>();
        fault.Message.Returns(inner);
        fault.Exceptions.Returns(Array.Empty<ExceptionInfo>());

        var context = Substitute.For<ConsumeContext<Fault<EntryStepDispatch>>>();
        context.Message.Returns(fault);
        context.CancellationToken.Returns(ct);
        // GetSendEndpoint returns a benign substitute so the DLQ/re-inject Send path does not NRE.
        context.GetSendEndpoint(Arg.Any<Uri>()).Returns(Task.FromResult(Substitute.For<ISendEndpoint>()));
        return context;
    }

    [Fact]
    public async Task Recovered_PublishesPauseThenResume_ForInnerWorkflow()
    {
        var ct = TestContext.Current.CancellationToken;

        var inner = SampleInner();
        // Up Redis → the probe loop returns Recovered on its first iteration.
        var recovery = new L2ProbeRecovery(new FakeRedis(FakeRedis.RedisHealth.Up).Multiplexer, Opts());
        var consumer = new FaultEntryStepDispatchConsumer(
            NullLogger<FaultEntryStepDispatchConsumer>.Instance, recovery);

        var context = FaultContext(inner, ct);

        await consumer.Consume(context);

        // Pause published at intake, Resume published on Recovered — carrying the inner workflow's id + H.
        await context.Received(1).Publish(Arg.Any<PauseWorkflow>(), Arg.Any<CancellationToken>());
        await context.Received(1).Publish(Arg.Any<ResumeWorkflow>(), Arg.Any<CancellationToken>());
        await context.Received(1).Publish(
            Arg.Is<PauseWorkflow>(p => p.WorkflowId == inner.WorkflowId && p.H == inner.H),
            Arg.Any<CancellationToken>());
        await context.Received(1).Publish(
            Arg.Is<ResumeWorkflow>(r => r.WorkflowId == inner.WorkflowId && r.H == inner.H),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GaveUp_PublishesPause_ButNoResume()
    {
        var ct = TestContext.Current.CancellationToken;

        var inner = SampleInner();
        // Down Redis → the probe loop exhausts MaxAttempts and returns GaveUp.
        var recovery = new L2ProbeRecovery(new FakeRedis(FakeRedis.RedisHealth.Down).Multiplexer, Opts());
        var consumer = new FaultEntryStepDispatchConsumer(
            NullLogger<FaultEntryStepDispatchConsumer>.Instance, recovery);

        var context = FaultContext(inner, ct);

        await consumer.Consume(context);

        // D-09: GaveUp publishes the intake Pause but NEVER a Resume (give-up → DLQ, no re-pin).
        await context.Received(1).Publish(Arg.Any<PauseWorkflow>(), Arg.Any<CancellationToken>());
        await context.DidNotReceive().Publish(Arg.Any<ResumeWorkflow>(), Arg.Any<CancellationToken>());
    }
}
