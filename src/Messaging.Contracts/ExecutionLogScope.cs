namespace Messaging.Contracts;

/// <summary>
/// Execution-id log-scope keys (LOG-03). Key strings MUST equal the structured-param names so a
/// value placed under a key here surfaces at the SAME Elasticsearch <c>attributes.&lt;Key&gt;</c>
/// field as the matching template parameter, via the existing OTel IncludeScopes+ParseStateValues
/// bridge. Sibling to <see cref="CorrelationKeys"/>; CorrelationId is deliberately NOT here — it
/// stays owned by <see cref="CorrelationKeys.LogScope"/> (D-01). Pure POCO leaf — no MassTransit ref.
/// </summary>
public static class ExecutionLogScope
{
    public const string WorkflowId  = "WorkflowId";
    public const string StepId      = "StepId";
    public const string ProcessorId = "ProcessorId";
    public const string ExecutionId = "ExecutionId";
    public const string EntryId     = "EntryId";

    /// <summary>
    /// Single source of truth for the 5-key execution-scope dict, shared by the bus-wide inbound
    /// consume filter and (next wave) the Keeper fault consumers that open the scope manually — the
    /// filter does NOT fire on <c>Fault&lt;T&gt;</c> (D-07). Byte-identical skip rules: each Guid is
    /// skipped when <c>Guid.Empty</c>, the string EntryId when null/empty; no CorrelationId key.
    /// </summary>
    public static Dictionary<string, object> BuildState(IExecutionCorrelated ec)
    {
        var state = new Dictionary<string, object>();
        if (ec.WorkflowId  != Guid.Empty) state[WorkflowId]  = ec.WorkflowId.ToString();
        if (ec.StepId      != Guid.Empty) state[StepId]      = ec.StepId.ToString();
        if (ec.ProcessorId != Guid.Empty) state[ProcessorId] = ec.ProcessorId.ToString();
        if (ec.ExecutionId != Guid.Empty) state[ExecutionId] = ec.ExecutionId.ToString();
        if (!string.IsNullOrEmpty(ec.EntryId)) state[EntryId] = ec.EntryId;
        return state;
    }
}
