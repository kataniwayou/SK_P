namespace Messaging.Contracts;

/// <summary>
/// Single source of truth for the WebApi responder queue endpoint names, shared between
/// the WebApi (binds the ReceiveEndpoint, Plan 02) and the Phase 26 processor request client
/// (sends to exchange:{name}). Mirrors OrchestratorQueues; bare short-names, no scheme prefix (D-06).
/// </summary>
public static class ProcessorQueues
{
    public const string IdentityQuery = "processor-identity-query";
    public const string SchemaQuery   = "schema-definition-query";
}
