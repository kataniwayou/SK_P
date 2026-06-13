using System.Text.Json.Serialization;

namespace Messaging.Contracts.Projections;

/// <summary>
/// L2 per-INSTANCE processor-liveness value (KEY-04 / STATE-01 / STATE-02) for the
/// skp:proc:{processorId}:{instanceId} key. Liveness-only by construction: it carries NO
/// inputDefinition/outputDefinition (dropped from L2). Isolated from the SHARED LivenessProjection
/// (D-01) so the out-of-scope workflow-root path is untouched. [property: JsonPropertyName] is
/// load-bearing (RESEARCH Pitfall 1). status is DERIVED from summary via Create(...) — the positional
/// ctor stays public ONLY for STJ deserialization (Phase-61 reader); Create is the ONLY sanctioned
/// construction path (STATE-01/02 invariant enforcement point — RESEARCH Pitfall 3).
/// </summary>
public sealed record ProcessorLivenessEntry(
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("interval")]  int Interval,
    [property: JsonPropertyName("status")]    string Status,
    [property: JsonPropertyName("summary")]   LivenessSummary Summary)
{
    /// <summary>
    /// Single enforcement point for the STATE-01/02 invariant. Each per-schema outcome is a
    /// SchemaOutcome string, or null = schema id absent = null-is-skip => Success (D-02 / D-02a).
    /// configSchema is the v6.0.0 Gate-A startup config-compat outcome (never recomputed). ANY Fail
    /// => status = LivenessStatus.Unhealthy; otherwise Healthy. The Phase-60 writer feeds outcomes in
    /// and CANNOT produce a status that contradicts the summary.
    /// </summary>
    public static ProcessorLivenessEntry Create(
        string? inputOutcome,
        string? outputOutcome,
        string? configOutcome,
        DateTime timestamp,
        int interval)
    {
        var input  = inputOutcome  ?? SchemaOutcome.Success;
        var output = outputOutcome ?? SchemaOutcome.Success;
        var config = configOutcome ?? SchemaOutcome.Success;

        var summary = new LivenessSummary(input, output, config);

        var anyFail = input  == SchemaOutcome.Fail
                   || output == SchemaOutcome.Fail
                   || config == SchemaOutcome.Fail;

        var status = anyFail ? LivenessStatus.Unhealthy : LivenessStatus.Healthy;

        return new ProcessorLivenessEntry(timestamp, interval, status, summary);
    }
}

/// <summary>
/// Per-schema liveness summary (STATE-02): each field is a SchemaOutcome string (SUCCESS|FAIL).
/// configSchema is the v6.0.0 Gate-A startup config-compat outcome (D-02a — never recomputed;
/// a null ConfigSchemaId => Success/null-is-skip). [property: JsonPropertyName] is load-bearing.
/// </summary>
public sealed record LivenessSummary(
    [property: JsonPropertyName("inputSchema")]  string InputSchema,
    [property: JsonPropertyName("outputSchema")] string OutputSchema,
    [property: JsonPropertyName("configSchema")] string ConfigSchema);
