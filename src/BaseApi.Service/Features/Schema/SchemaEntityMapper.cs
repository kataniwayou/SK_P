using BaseApi.Core.Mapping;
using Riok.Mapperly.Abstractions;

namespace BaseApi.Service.Features.Schema;

/// <summary>
/// Mapperly source-generated mapper for <see cref="SchemaEntity"/>. Compile-success of
/// this partial under <c>Directory.Build.props</c> RMG007/RMG012/RMG020/RMG089 promotion
/// (Plan 06-01) IS the SC#1 build-half proof — Mapperly 4.x's strict
/// <c>RequiredMappingStrategy = Both</c> fires RMG012 (target unmapped) and RMG020 (source
/// unmapped) by default; the 10 <see cref="MapperIgnoreTargetAttribute"/>s explicitly
/// suppress the 5 server-side fields on <see cref="SchemaEntity"/> that are NOT present
/// on the Create/Update DTOs (Id + 4 audit fields), preserving drift detection: adding a
/// new property to <see cref="SchemaEntity"/> WITHOUT wiring it through the DTOs (or adding
/// it to this ignore list) still fires the build error.
/// <para>
/// <b>No <see cref="MapperIgnoreSourceAttribute"/> on <see cref="ToRead"/></b> — per HTTP-07
/// <see cref="SchemaReadDto"/> carries Id + the 4 audit fields, so the source-side (entity)
/// fields are all mapped to target-side (DTO) members. The ToRead method is symmetric and
/// needs zero ignores (08-PATTERNS §C lines 188-189; 06-CONTEXT D-08 amended Claude's Discretion).
/// </para>
/// </summary>
[Mapper]
public sealed partial class SchemaEntityMapper :
    IEntityMapper<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto>
{
    [MapperIgnoreTarget(nameof(SchemaEntity.Id))]
    [MapperIgnoreTarget(nameof(SchemaEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(SchemaEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(SchemaEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(SchemaEntity.UpdatedBy))]
    public partial SchemaEntity ToEntity(SchemaCreateDto dto);

    [MapperIgnoreTarget(nameof(SchemaEntity.Id))]
    [MapperIgnoreTarget(nameof(SchemaEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(SchemaEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(SchemaEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(SchemaEntity.UpdatedBy))]
    public partial void Update(SchemaUpdateDto dto, SchemaEntity target);

    public partial SchemaReadDto ToRead(SchemaEntity entity);
}
