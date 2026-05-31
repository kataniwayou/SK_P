namespace Messaging.Contracts;

/// <summary>
/// Single source of truth for orchestrator queue endpoint names shared between the
/// orchestrator (binds the <c>ReceiveEndpoint</c>) and a future processor (Sends to it).
/// Mirrors the <c>L2ProjectionKeys</c> static-class single-source-of-truth shape (D-03).
/// </summary>
public static class OrchestratorQueues
{
    /// <summary>
    /// Shared competing-consumer result queue short-name (D-03). Bind it as
    /// <c>ReceiveEndpoint(OrchestratorQueues.Result)</c>; a sender prepends the
    /// <c>queue:</c> URI scheme. The bare endpoint short-name is stored WITHOUT the
    /// scheme prefix — the Send side adds it.
    /// </summary>
    public const string Result = "orchestrator-result";
}
