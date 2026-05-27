using BaseApi.Core.Mapping;
using Riok.Mapperly.Abstractions;

namespace BaseApi.Service.Features.Processor;

/// <summary>
/// Mapperly source-generated mapper for <see cref="ProcessorEntity"/>. Compile-success of
/// this partial under <c>Directory.Build.props</c> RMG007/RMG012/RMG020/RMG089 promotion
/// (Plan 06-01) IS the build-half drift detection — Mapperly 4.x's strict
/// <c>RequiredMappingStrategy = Both</c> fires RMG012 (target unmapped) and RMG020 (source
/// unmapped) by default; the 10 <see cref="MapperIgnoreTargetAttribute"/>s explicitly
/// suppress the 5 server-side fields on <see cref="ProcessorEntity"/> that are NOT present
/// on the Create/Update DTOs (Id + 4 audit fields). Adding a new property to
/// <see cref="ProcessorEntity"/> WITHOUT wiring it through the DTOs still fires the build error.
/// <para>
/// <b>No <see cref="MapperIgnoreSourceAttribute"/> on <see cref="ToRead"/></b> — per HTTP-07
/// <see cref="ProcessorReadDto"/> carries Id + the 4 audit fields, so source-side (entity)
/// fields are all mapped to target-side (DTO) members. The ToRead method is symmetric and
/// needs zero ignores (08-PATTERNS §C lines 188-189; 06-CONTEXT D-08 amended).
/// </para>
/// </summary>
[Mapper]
public sealed partial class ProcessorEntityMapper :
    IEntityMapper<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto>
{
    [MapperIgnoreTarget(nameof(ProcessorEntity.Id))]
    [MapperIgnoreTarget(nameof(ProcessorEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(ProcessorEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(ProcessorEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(ProcessorEntity.UpdatedBy))]
    public partial ProcessorEntity ToEntity(ProcessorCreateDto dto);

    [MapperIgnoreTarget(nameof(ProcessorEntity.Id))]
    [MapperIgnoreTarget(nameof(ProcessorEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(ProcessorEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(ProcessorEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(ProcessorEntity.UpdatedBy))]
    public partial void Update(ProcessorUpdateDto dto, ProcessorEntity target);

    public partial ProcessorReadDto ToRead(ProcessorEntity entity);
}
