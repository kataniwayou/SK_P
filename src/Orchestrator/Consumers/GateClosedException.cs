namespace Orchestrator.Consumers;

/// <summary>
/// Gate-closed signal (D-06): the startup gate is not yet open (hydration incomplete) when a
/// consumer is invoked. Unlike <see cref="WorkflowRootNotFoundException"/> (a business outcome that
/// IS <c>Ignore&lt;&gt;</c>d so it never retries), this exception MUST flow to the redelivery
/// middleware so the message is re-delivered after hydration completes — it is therefore NEVER added
/// to any <c>r.Ignore&lt;&gt;()</c> list. Role analog: <see cref="WorkflowRootNotFoundException"/>.
/// </summary>
public sealed class GateClosedException()
    : Exception("Startup gate is closed — message will be redelivered after hydration completes.");
