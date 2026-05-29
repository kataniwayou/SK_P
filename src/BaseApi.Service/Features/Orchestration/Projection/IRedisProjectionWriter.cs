using BaseApi.Service.Features.Orchestration;

namespace BaseApi.Service.Features.Orchestration.Projection;

internal interface IRedisProjectionWriter
{
    Task UpsertAsync(WorkflowGraphSnapshot snapshot, CancellationToken ct);
}
