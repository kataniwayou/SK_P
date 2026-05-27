using BaseApi.Core.Contracts;
using BaseApi.Core.Validation;

namespace BaseApi.Service.Features.Step;

/// <summary>
/// Create-side DTO. Excludes server-controlled fields (Id, CreatedAt, UpdatedAt,
/// CreatedBy, UpdatedBy) per HTTP-05. Carries <c>NextStepIds</c> List of Guid which
/// lives ONLY on DTOs (NOT on <see cref="StepEntity"/>) — the M2M junction
/// <c>StepNextSteps</c> is the source of truth, populated by
/// <c>StepService.SyncJunctionsAsync</c> between repo.Add and SaveChanges.
/// </summary>
public sealed record StepCreateDto(
    string Name,
    string Version,
    string? Description,
    Guid ProcessorId,
    List<Guid>? NextStepIds,
    StepEntryCondition EntryCondition) : IBaseDto;

/// <summary>
/// Update-side DTO. Excludes server-controlled fields per HTTP-06. On update,
/// existing <c>StepNextSteps</c> rows for this Step.Id are REMOVED before the new
/// <c>NextStepIds</c> values are INSERTed (override of <c>SyncJunctionsAsync</c>).
/// </summary>
public sealed record StepUpdateDto(
    string Name,
    string Version,
    string? Description,
    Guid ProcessorId,
    List<Guid>? NextStepIds,
    StepEntryCondition EntryCondition) : IBaseDto;

/// <summary>
/// Read-side DTO returned to clients. Carries <c>Id</c> + 4 audit fields per HTTP-07.
/// <para>
/// <b>v1 limitation:</b> <c>NextStepIds</c> is NOT populated by the Mapperly
/// <c>ToRead</c> method (the source entity lacks this property by design). Phase 8
/// v1 ships the DTO with <c>NextStepIds = null</c> on GET / List paths; the junction
/// rows persisted by <c>SyncJunctionsAsync</c> are the source of truth and can be
/// asserted via direct DB queries in tests. Post-ToRead enrichment is deferred to a
/// future phase if BaseService GET/List methods become virtual.
/// </para>
/// </summary>
public sealed record StepReadDto(
    Guid Id,
    string Name,
    string Version,
    string? Description,
    Guid ProcessorId,
    List<Guid>? NextStepIds,
    StepEntryCondition EntryCondition,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy) : IBaseDto, IHasId;
