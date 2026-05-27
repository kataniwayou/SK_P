using BaseApi.Core.Mapping;
using Riok.Mapperly.Abstractions;

namespace BaseApi.Service.Features.Step;

/// <summary>
/// Mapperly source-generated mapper for <see cref="StepEntity"/>. Compile-success of this
/// partial under <c>Directory.Build.props</c> RMG007/RMG012/RMG020/RMG089 promotion
/// (Plan 06-01) IS the build-half drift detection.
/// <para>
/// <b>Asymmetric attribute coverage</b> (08-RESEARCH §Mapperly Patterns / Step subsection
/// lines 419-428): the next-step collection lives on the DTOs but NOT on the entity, so
/// the mapper requires:
/// </para>
/// <list type="bullet">
///   <item><see cref="ToEntity"/>: 5 <see cref="MapperIgnoreTargetAttribute"/> for server
///     fields (Id + 4 audit) + 1 <see cref="MapperIgnoreSourceAttribute"/> for the
///     next-step collection on the source DTO (Mapperly RMG020 fires because the source
///     carries a member the target entity lacks).</item>
///   <item><see cref="Update"/>: same 5+1 pattern targeting <see cref="StepUpdateDto"/>.</item>
///   <item><see cref="ToRead"/>: 1 <c>[MapValue(...., null)]</c> for the next-step
///     collection on the target DTO. Because <see cref="StepReadDto"/> is a positional
///     record, <c>NextStepIds</c> is a required constructor parameter — Mapperly cannot
///     simply ignore it (RMG013 would fire). <c>MapValue</c> assigns <c>null</c> directly
///     to the constructor parameter; v1 ships with <c>NextStepIds = null</c> on GET / List
///     paths (junction-row enrichment deferred — see <see cref="StepReadDto"/> remarks).</item>
/// </list>
/// <para>
/// Totals: 10 <c>[MapperIgnoreTarget]</c> + 2 <c>[MapperIgnoreSource]</c> + 1 <c>[MapValue]</c>.
/// This asymmetric shape is unique to Step (and will be mirrored for Workflow in Plan 08-06
/// which carries two M2M collections).
/// </para>
/// </summary>
[Mapper]
public sealed partial class StepEntityMapper :
    IEntityMapper<StepEntity, StepCreateDto, StepUpdateDto, StepReadDto>
{
    [MapperIgnoreTarget(nameof(StepEntity.Id))]
    [MapperIgnoreTarget(nameof(StepEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(StepEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(StepEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(StepEntity.UpdatedBy))]
    [MapperIgnoreSource(nameof(StepCreateDto.NextStepIds))]
    public partial StepEntity ToEntity(StepCreateDto dto);

    [MapperIgnoreTarget(nameof(StepEntity.Id))]
    [MapperIgnoreTarget(nameof(StepEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(StepEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(StepEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(StepEntity.UpdatedBy))]
    [MapperIgnoreSource(nameof(StepUpdateDto.NextStepIds))]
    public partial void Update(StepUpdateDto dto, StepEntity target);

    [MapValue(nameof(StepReadDto.NextStepIds), null)]
    public partial StepReadDto ToRead(StepEntity entity);
}
