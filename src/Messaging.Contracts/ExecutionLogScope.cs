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
}
