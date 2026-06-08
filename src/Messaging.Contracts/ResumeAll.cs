namespace Messaging.Contracts;

/// <summary>Global control broadcast (D-08/A14): resume ALL orchestrator jobs per-job (L2 healthy). No H — pure broadcast, CorrelationId for tracing only (RETIRE-01 no-H posture).</summary>
public sealed record ResumeAll : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
