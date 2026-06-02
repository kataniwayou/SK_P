using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Contracts;

/// <summary>
/// LOG-03: pins each <see cref="ExecutionLogScope"/> key string to its structured-param name so a
/// value placed under a key here surfaces at the SAME Elasticsearch <c>attributes.&lt;Key&gt;</c> field
/// as the matching template parameter (via the existing OTel IncludeScopes+ParseStateValues bridge).
/// If a key string ever drifts from its param name, scope-derived and template-param-derived
/// attributes would split across two ES fields — this test fails first.
/// </summary>
public sealed class ExecutionLogScopeKeyTests
{
    [Fact]
    public void Keys_Equal_Their_Param_Names()
    {
        Assert.Equal("WorkflowId",  ExecutionLogScope.WorkflowId);
        Assert.Equal("StepId",      ExecutionLogScope.StepId);
        Assert.Equal("ProcessorId", ExecutionLogScope.ProcessorId);
        Assert.Equal("ExecutionId", ExecutionLogScope.ExecutionId);
        Assert.Equal("EntryId",     ExecutionLogScope.EntryId);
    }
}
