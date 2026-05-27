using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Repositories;
using BaseApi.Core.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BaseApi.Service.Features.Step;

/// <summary>
/// First Phase 8 entity service that overrides <see cref="SyncJunctionsAsync"/> to
/// persist the next-step collection into the <see cref="StepNextSteps"/> junction
/// table. The override runs between <c>repo.AddAsync</c> (tracker: Added) and
/// <c>SaveChangesAsync</c> in the locked 6-step <c>CreateAsync</c> verb order — all
/// staged changes commit atomically in the same EF Core transaction (Phase 7 D-11).
/// <para>
/// <b>Behavior:</b>
/// <list type="bullet">
///   <item><b>Create</b> (createDto non-null): inserts one <see cref="StepNextSteps"/>
///     row per entry in <see cref="StepCreateDto.NextStepIds"/>.</item>
///   <item><b>Update</b> (updateDto non-null): first REMOVES all existing junction rows
///     where <c>StepId == entity.Id</c>, then INSERTS one row per entry in
///     <see cref="StepUpdateDto.NextStepIds"/>. Equivalent to a remove-and-replace
///     semantic — clients submit the desired final state.</item>
/// </list>
/// </para>
/// </summary>
public sealed class StepService :
    BaseService<StepEntity, StepCreateDto, StepUpdateDto, StepReadDto>
{
    public StepService(
        IValidator<StepCreateDto> createValidator,
        IValidator<StepUpdateDto> updateValidator,
        IEntityMapper<StepEntity, StepCreateDto, StepUpdateDto, StepReadDto> mapper,
        IRepository<StepEntity> repo,
        BaseDbContext dbContext)
        : base(createValidator, updateValidator, mapper, repo, dbContext) { }

    /// <summary>
    /// Synchronize <see cref="StepNextSteps"/> junction rows with the next-step
    /// collection on the provided DTO. Called between <c>repo.AddAsync</c> /
    /// <c>mapper.Update</c> and <c>SaveChangesAsync</c> in the locked 6-step verb order.
    /// </summary>
    protected override async Task SyncJunctionsAsync(
        StepEntity entity,
        StepCreateDto? createDto,
        StepUpdateDto? updateDto,
        CancellationToken ct)
    {
        var junctionSet = DbContext.Set<StepNextSteps>();

        // On Update: remove existing junction rows for this entity.Id before adding new ones.
        // Remove-and-replace semantics — clients submit the desired final state.
        if (updateDto is not null)
        {
            var existing = await junctionSet
                .Where(j => j.StepId == entity.Id)
                .ToListAsync(ct);
            if (existing.Count > 0)
            {
                junctionSet.RemoveRange(existing);
            }
        }

        var newIds = createDto?.NextStepIds ?? updateDto?.NextStepIds;
        if (newIds is { Count: > 0 })
        {
            var rows = newIds.Select(nextId => new StepNextSteps
            {
                StepId = entity.Id,
                NextStepId = nextId,
            });
            await junctionSet.AddRangeAsync(rows, ct);
        }
    }
}
