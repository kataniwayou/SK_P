using BaseApi.Service.Features.Orchestration;

namespace BaseApi.Service.Features.Orchestration.Projection;

internal interface IRedisProjectionWriter
{
    /// <summary>
    /// Projects a single-workflow <see cref="WorkflowGraphSnapshot"/> into the three L2
    /// (Redis) keyspaces. The writer stays HTTP-agnostic (D-01): <paramref name="correlationId"/>
    /// is resolved once by <c>OrchestrationService</c> from the originating Start request's
    /// <c>X-Correlation-Id</c> and passed in explicitly — the writer never touches HttpContext.
    /// </summary>
    Task UpsertAsync(WorkflowGraphSnapshot snapshot, string correlationId, CancellationToken ct);
}
