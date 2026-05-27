using BaseApi.Core.Contracts;
using BaseApi.Core.Validation;

namespace BaseApi.Service.Features.Schema;

/// <summary>
/// Create-side DTO. Excludes server-controlled fields (Id, CreatedAt, UpdatedAt,
/// CreatedBy, UpdatedBy) per HTTP-05; Mapperly cannot map what isn't on the source.
/// </summary>
public sealed record SchemaCreateDto(
    string Name,
    string Version,
    string? Description,
    string Definition) : IBaseDto;

/// <summary>
/// Update-side DTO. Excludes server-controlled fields per HTTP-06; Mapperly cannot
/// map what isn't on the source. The <c>Update</c> mapper method also declares
/// <c>[MapperIgnoreTarget]</c> for the 5 entity-side fields not present here.
/// </summary>
public sealed record SchemaUpdateDto(
    string Name,
    string Version,
    string? Description,
    string Definition) : IBaseDto;

/// <summary>
/// Read-side DTO returned to clients. Carries <c>Id</c> + 4 audit fields per HTTP-07
/// (Read DTOs include server-side fields). Implements <see cref="IHasId"/> so
/// <c>BaseController.Create</c> can read <c>read.Id</c> in <c>CreatedAtAction</c>.
/// Implements <see cref="IBaseDto"/> for narrative-field symmetry across Create/Update/Read.
/// </summary>
public sealed record SchemaReadDto(
    Guid Id,
    string Name,
    string Version,
    string? Description,
    string Definition,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy) : IBaseDto, IHasId;
