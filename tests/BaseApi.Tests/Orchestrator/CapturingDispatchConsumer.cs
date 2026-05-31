using MassTransit;
using Messaging.Contracts;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Synthetic in-memory consumer of <see cref="EntryStepDispatch"/> bound to
/// <c>ReceiveEndpoint($"{processorId:D}")</c> — the short-name endpoint that a
/// <c>Send</c> to <c>queue:{processorId:D}</c> targets (RESEARCH "Synthetic test consumer",
/// assumption A2). The <see cref="ITestHarness"/> <c>Consumed</c> filter does the actual capturing;
/// <see cref="Consume"/> is a no-op so the harness records the message without side effects.
/// <para>
/// Reused by the FIRE (ORCH-FIRE-01) and CONSUME (ORCH-CONSUME-01) harness tests.
/// </para>
/// </summary>
public sealed class CapturingDispatchConsumer : IConsumer<EntryStepDispatch>
{
    public Task Consume(ConsumeContext<EntryStepDispatch> context) => Task.CompletedTask;
}
