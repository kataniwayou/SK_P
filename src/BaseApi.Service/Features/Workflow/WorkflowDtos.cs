using BaseApi.Core.Contracts;
using BaseApi.Core.Validation;

namespace BaseApi.Service.Features.Workflow;

/// <summary>
/// Create-side DTO. Excludes server-controlled fields (Id, CreatedAt, UpdatedAt,
/// CreatedBy, UpdatedBy) per HTTP-05. Carries TWO M2M collections that live ONLY
/// on the DTO (NOT on <see cref="WorkflowEntity"/>) — the junctions
/// <c>WorkflowEntrySteps</c> and <c>WorkflowAssignments</c> are the source of
/// truth, populated by <c>WorkflowService.SyncJunctionsAsync</c> between repo.Add
/// and SaveChanges.
/// <para>
/// <c>EntryStepIds</c> is REQUIRED non-empty per VALID-17. <c>AssignmentIds</c> is
/// nullable per ENTITY-08 (Workflow may have no assignments). <c>CronExpression</c>
/// is nullable per ENTITY-08 (null = not scheduled); when non-null it MUST parse as
/// a 5-field Cronos.CronExpression (VALID-19; SC#4).
/// </para>
/// </summary>
public sealed record WorkflowCreateDto(
    string Name,
    string Version,
    string? Description,
    List<Guid> EntryStepIds,
    List<Guid>? AssignmentIds,
    string? CronExpression) : IBaseDto;

/// <summary>
/// Update-side DTO. Excludes server-controlled fields per HTTP-06. On update,
/// existing <c>WorkflowEntrySteps</c> AND <c>WorkflowAssignments</c> rows for this
/// Workflow.Id are REMOVED before the new collection values are INSERTed (override
/// of <c>SyncJunctionsAsync</c> handles BOTH junctions remove-and-replace).
/// </summary>
public sealed record WorkflowUpdateDto(
    string Name,
    string Version,
    string? Description,
    List<Guid> EntryStepIds,
    List<Guid>? AssignmentIds,
    string? CronExpression) : IBaseDto;

/// <summary>
/// Read-side DTO returned to clients. Carries <c>Id</c> + 4 audit fields per HTTP-07.
/// <para>
/// <b>v1 limitation:</b> <c>EntryStepIds</c> and <c>AssignmentIds</c> are NOT
/// populated by the Mapperly <c>ToRead</c> method (the source entity lacks both
/// properties by design). Phase 8 v1 ships the DTO with both collections set to
/// <c>null</c> on GET / List paths; the junction rows persisted by
/// <c>SyncJunctionsAsync</c> are the source of truth and can be asserted via
/// direct DB queries in tests. Post-ToRead enrichment is deferred to a future
/// phase (same pattern as <c>StepReadDto.NextStepIds</c> in Plan 08-04).
/// </para>
/// </summary>
public sealed record WorkflowReadDto(
    Guid Id,
    string Name,
    string Version,
    string? Description,
    List<Guid> EntryStepIds,
    List<Guid>? AssignmentIds,
    string? CronExpression,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy) : IBaseDto, IHasId;
