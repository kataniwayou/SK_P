namespace Messaging.Contracts;

/// <summary>Global control broadcast (D-08/A14): pause ALL orchestrator jobs (L2 unhealthy). No H — pure broadcast, CorrelationId for tracing only (RETIRE-01 no-H posture).</summary>
public sealed record PauseAll : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
