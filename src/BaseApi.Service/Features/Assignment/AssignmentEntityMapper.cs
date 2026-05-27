using BaseApi.Core.Mapping;
using Riok.Mapperly.Abstractions;

namespace BaseApi.Service.Features.Assignment;

/// <summary>
/// Mapperly source-generated mapper for <see cref="AssignmentEntity"/>. Mirrors the
/// Schema/Processor pattern: 10 <see cref="MapperIgnoreTargetAttribute"/>s
/// (5 on <see cref="ToEntity"/> + 5 on <see cref="Update"/>) for the 5 server-side
/// fields on <see cref="AssignmentEntity"/> that are NOT present on the Create/Update
/// DTOs (Id + 4 audit fields). Adding a new property to <see cref="AssignmentEntity"/>
/// WITHOUT wiring it through the DTOs (or adding it to this ignore list) still fires
/// the Mapperly RMG012 build error.
/// <para>
/// <b>No <see cref="MapperIgnoreSourceAttribute"/> on <see cref="ToRead"/></b> — per
/// HTTP-07 <see cref="AssignmentReadDto"/> carries Id + the 4 audit fields, so the
/// source-side (entity) fields are all mapped to target-side (DTO) members. The ToRead
/// method is symmetric and needs zero ignores (audit-symmetric pattern from
/// Plans 08-02 / 08-03).
/// </para>
/// </summary>
[Mapper]
public sealed partial class AssignmentEntityMapper :
    IEntityMapper<AssignmentEntity, AssignmentCreateDto, AssignmentUpdateDto, AssignmentReadDto>
{
    [MapperIgnoreTarget(nameof(AssignmentEntity.Id))]
    [MapperIgnoreTarget(nameof(AssignmentEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(AssignmentEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(AssignmentEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(AssignmentEntity.UpdatedBy))]
    public partial AssignmentEntity ToEntity(AssignmentCreateDto dto);

    [MapperIgnoreTarget(nameof(AssignmentEntity.Id))]
    [MapperIgnoreTarget(nameof(AssignmentEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(AssignmentEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(AssignmentEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(AssignmentEntity.UpdatedBy))]
    public partial void Update(AssignmentUpdateDto dto, AssignmentEntity target);

    public partial AssignmentReadDto ToRead(AssignmentEntity entity);
}
