namespace Messaging.Contracts;

/// <summary>Cross-service correlation log-scope key. MUST equal the literal
/// CorrelationIdMiddleware uses so OTel IncludeScopes serializes one Elasticsearch attribute.</summary>
public static class CorrelationKeys
{
    public const string LogScope = "CorrelationId";
}
