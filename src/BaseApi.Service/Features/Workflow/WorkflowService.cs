using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Repositories;
using BaseApi.Core.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BaseApi.Service.Features.Workflow;

/// <summary>
/// Workflow entity service — second Phase 8 service that overrides
/// <see cref="SyncJunctionsAsync"/>. Unlike <c>StepService</c> (one junction), Workflow
/// has TWO junctions: <see cref="WorkflowEntrySteps"/> (M2M to Step on entry path) and
/// <see cref="WorkflowAssignments"/> (M2M to Assignment). The override syncs both in a
/// single SaveChangesAsync transaction.
/// <para>
/// The override runs between <c>repo.AddAsync</c> (tracker: Added) and
/// <c>SaveChangesAsync</c> in the locked 6-step <c>CreateAsync</c> verb order — all
/// staged changes commit atomically in the same EF Core transaction (Phase 7 D-11).
/// </para>
/// <para>
/// <b>Behavior per junction:</b>
/// <list type="bullet">
///   <item><b>Create</b> (createDto non-null): inserts one row per entry in
///     <see cref="WorkflowCreateDto.EntryStepIds"/> (NotEmpty per VALID-17) and one row
///     per entry in <see cref="WorkflowCreateDto.AssignmentIds"/> (nullable per
///     VALID-18 — only inserts when non-null and non-empty).</item>
///   <item><b>Update</b> (updateDto non-null): first REMOVES all existing junction rows
///     where <c>WorkflowId == entity.Id</c> on BOTH junctions, then INSERTS one row per
///     entry in the new collections. Remove-and-replace semantics — clients submit the
///     desired final state for both collections.</item>
/// </list>
/// </para>
/// </summary>
public sealed class WorkflowService :
    BaseService<WorkflowEntity, WorkflowCreateDto, WorkflowUpdateDto, WorkflowReadDto>
{
    public WorkflowService(
        IValidator<WorkflowCreateDto> createValidator,
        IValidator<WorkflowUpdateDto> updateValidator,
        IEntityMapper<WorkflowEntity, WorkflowCreateDto, WorkflowUpdateDto, WorkflowReadDto> mapper,
        IRepository<WorkflowEntity> repo,
        BaseDbContext dbContext)
        : base(createValidator, updateValidator, mapper, repo, dbContext) { }

    /// <summary>
    /// Synchronize <see cref="WorkflowEntrySteps"/> AND <see cref="WorkflowAssignments"/>
    /// junction rows with the EntryStepIds + AssignmentIds collections on the provided
    /// DTO. Called between <c>repo.AddAsync</c> / <c>mapper.Update</c> and
    /// <c>SaveChangesAsync</c> in the locked 6-step verb order.
    /// </summary>
    protected override async Task SyncJunctionsAsync(
        WorkflowEntity entity,
        WorkflowCreateDto? createDto,
        WorkflowUpdateDto? updateDto,
        CancellationToken ct)
    {
        var entryStepsSet = DbContext.Set<WorkflowEntrySteps>();
        var assignmentsSet = DbContext.Set<WorkflowAssignments>();

        // On Update: remove existing junction rows for this entity.Id on BOTH junctions
        // before adding new ones. Remove-and-replace semantics — clients submit the
        // desired final state for both collections.
        if (updateDto is not null)
        {
            var existingEntrySteps = await entryStepsSet
                .Where(j => j.WorkflowId == entity.Id)
                .ToListAsync(ct);
            if (existingEntrySteps.Count > 0)
            {
                entryStepsSet.RemoveRange(existingEntrySteps);
            }

            var existingAssignments = await assignmentsSet
                .Where(j => j.WorkflowId == entity.Id)
                .ToListAsync(ct);
            if (existingAssignments.Count > 0)
            {
                assignmentsSet.RemoveRange(existingAssignments);
            }
        }

        // VALID-17: EntryStepIds is required non-empty per the DTO+validator contract.
        var entryStepIds = createDto?.EntryStepIds ?? updateDto?.EntryStepIds ?? new List<Guid>();
        if (entryStepIds.Count > 0)
        {
            var rows = entryStepIds.Select(stepId => new WorkflowEntrySteps
            {
                WorkflowId = entity.Id,
                StepId = stepId,
            });
            await entryStepsSet.AddRangeAsync(rows, ct);
        }

        // AssignmentIds is nullable per VALID-18 — only insert when non-null + non-empty.
        var assignmentIds = createDto?.AssignmentIds ?? updateDto?.AssignmentIds;
        if (assignmentIds is { Count: > 0 })
        {
            var rows = assignmentIds.Select(assignmentId => new WorkflowAssignments
            {
                WorkflowId = entity.Id,
                AssignmentId = assignmentId,
            });
            await assignmentsSet.AddRangeAsync(rows, ct);
        }
    }
}
