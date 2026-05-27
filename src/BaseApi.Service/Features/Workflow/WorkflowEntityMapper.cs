using BaseApi.Core.Mapping;
using Riok.Mapperly.Abstractions;

namespace BaseApi.Service.Features.Workflow;

/// <summary>
/// Mapperly source-generated mapper for <see cref="WorkflowEntity"/>. Compile-success of
/// this partial under <c>Directory.Build.props</c> RMG007/RMG012/RMG020/RMG089 promotion
/// (Plan 06-01) IS the build-half drift detection.
/// <para>
/// <b>Asymmetric attribute coverage</b> (PATTERNS §C Workflow subsection): the
/// entry-step + assignment collections live on the DTOs but NOT on the entity, so the
/// mapper requires:
/// </para>
/// <list type="bullet">
///   <item><see cref="ToEntity"/>: 5 <see cref="MapperIgnoreTargetAttribute"/> for server
///     fields (Id + 4 audit) + 2 <see cref="MapperIgnoreSourceAttribute"/> for the
///     entry-step + assignment collections on the source DTO (Mapperly RMG020 fires
///     because the source carries members the target entity lacks).</item>
///   <item><see cref="Update"/>: same 5+2 pattern targeting <see cref="WorkflowUpdateDto"/>.</item>
///   <item><see cref="ToRead"/>: 2 <c>[MapValue(..., null)]</c> for the entry-step +
///     assignment collections on the target DTO. Because <see cref="WorkflowReadDto"/>
///     is a positional record, both collections are required constructor parameters —
///     Mapperly cannot simply ignore them (RMG013 would fire — same trap that Plan
///     08-04 Step hit). <c>MapValue</c> assigns <c>null</c> directly to the constructor
///     parameter; v1 ships with both collections <c>null</c> on GET / List paths
///     (junction-row enrichment deferred — see <see cref="WorkflowReadDto"/> remarks).
///     Note: <see cref="WorkflowReadDto.EntryStepIds"/> is declared
///     <c>List&lt;Guid&gt;</c> (non-nullable) in the contract, but the v1 read path
///     ships <c>null</c> per the deferred-enrichment design; consumers MUST treat the
///     read-side EntryStepIds as nullable on v1 reads (documented v1 limitation).</item>
/// </list>
/// <para>
/// Totals: 10 <c>[MapperIgnoreTarget]</c> (5+5) + 4 <c>[MapperIgnoreSource]</c> (2+2) +
/// 2 <c>[MapValue]</c> (2 on ToRead). Same RMG013 mitigation pattern as Plan 08-04 Step.
/// </para>
/// </summary>
[Mapper]
public sealed partial class WorkflowEntityMapper :
    IEntityMapper<WorkflowEntity, WorkflowCreateDto, WorkflowUpdateDto, WorkflowReadDto>
{
    [MapperIgnoreTarget(nameof(WorkflowEntity.Id))]
    [MapperIgnoreTarget(nameof(WorkflowEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(WorkflowEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(WorkflowEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(WorkflowEntity.UpdatedBy))]
    [MapperIgnoreSource(nameof(WorkflowCreateDto.EntryStepIds))]
    [MapperIgnoreSource(nameof(WorkflowCreateDto.AssignmentIds))]
    public partial WorkflowEntity ToEntity(WorkflowCreateDto dto);

    [MapperIgnoreTarget(nameof(WorkflowEntity.Id))]
    [MapperIgnoreTarget(nameof(WorkflowEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(WorkflowEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(WorkflowEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(WorkflowEntity.UpdatedBy))]
    [MapperIgnoreSource(nameof(WorkflowUpdateDto.EntryStepIds))]
    [MapperIgnoreSource(nameof(WorkflowUpdateDto.AssignmentIds))]
    public partial void Update(WorkflowUpdateDto dto, WorkflowEntity target);

    // ToRead: target ReadDto carries EntryStepIds + AssignmentIds; entity lacks both.
    // WorkflowReadDto is a positional record so the two collections are required ctor
    // params — MapperIgnoreTarget can't skip them (RMG013). MapValue supplies null
    // directly to the positional record ctor; v1 ships with both collections null on
    // GET / List paths. Tests assert junction-row state via direct DB queries.
    [MapValue(nameof(WorkflowReadDto.EntryStepIds), null)]
    [MapValue(nameof(WorkflowReadDto.AssignmentIds), null)]
    public partial WorkflowReadDto ToRead(WorkflowEntity entity);
}
