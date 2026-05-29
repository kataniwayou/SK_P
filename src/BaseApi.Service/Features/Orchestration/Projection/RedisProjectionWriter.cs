using BaseApi.Service.Features.Orchestration;

namespace BaseApi.Service.Features.Orchestration.Projection;

internal sealed class RedisProjectionWriter : IRedisProjectionWriter
{
    public Task UpsertAsync(WorkflowGraphSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
}
