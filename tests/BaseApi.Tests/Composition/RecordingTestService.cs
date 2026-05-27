using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Repositories;
using BaseApi.Core.Services;
using BaseApi.Tests.Validation;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BaseApi.Tests.Composition;

/// <summary>
/// BaseService subclass for SC#2 6-step ordering proof. Overrides SyncJunctionsAsync to record
/// <c>(DateTime.UtcNow, DbContext.ChangeTracker.Entries&lt;TestEntity&gt;().Single().State)</c> —
/// the tracker state at SyncJunctionsAsync entry MUST be <see cref="EntityState.Added"/>,
/// proving Step 4 runs AFTER Step 3 (repo.Add) and BEFORE Step 5 (SaveChanges).
/// </summary>
public sealed class RecordingTestService
    : BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>
{
    public List<(DateTime Timestamp, EntityState ChangeTrackerState)> JunctionRecords { get; } = new();

    public RecordingTestService(
        IValidator<TestCreateDto> createValidator,
        IValidator<TestUpdateDto> updateValidator,
        IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto> mapper,
        IRepository<TestEntity> repo,
        BaseDbContext dbContext,
        ILogger<BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>> logger)
        : base(createValidator, updateValidator, mapper, repo, dbContext, logger) { }

    protected override Task SyncJunctionsAsync(
        TestEntity entity, TestCreateDto? createDto, TestUpdateDto? updateDto, CancellationToken ct)
    {
        // Reads via the `protected BaseDbContext DbContext { get; }` property exposed by
        // BaseService (RESEARCH Pitfall 3).
        var state = DbContext.ChangeTracker.Entries<TestEntity>().Single().State;
        JunctionRecords.Add((Timestamp: DateTime.UtcNow, ChangeTrackerState: state));
        return Task.CompletedTask;
    }
}
