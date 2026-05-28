using BaseApi.Core.Contracts;
using BaseApi.Core.Validation;

namespace BaseApi.Service.Features.Processor;

/// <summary>
/// Create-side DTO. Excludes server-controlled fields (Id, CreatedAt, UpdatedAt,
/// CreatedBy, UpdatedBy) per HTTP-05; Mapperly cannot map what isn't on the source.
/// 7 positional params: Name, Version, Description, SourceHash, InputSchemaId, OutputSchemaId, ConfigSchemaId.
/// </summary>
public sealed record ProcessorCreateDto(
    string Name,
    string Version,
    string? Description,
    string SourceHash,
    Guid? InputSchemaId,
    Guid? OutputSchemaId,
    Guid? ConfigSchemaId) : IBaseDto;

/// <summary>
/// Update-side DTO. Excludes server-controlled fields per HTTP-06; Mapperly cannot
/// map what isn't on the source. The <c>Update</c> mapper method also declares
/// <c>[MapperIgnoreTarget]</c> for the 5 entity-side fields not present here.
/// 7 positional params: Name, Version, Description, SourceHash, InputSchemaId, OutputSchemaId, ConfigSchemaId.
/// </summary>
public sealed record ProcessorUpdateDto(
    string Name,
    string Version,
    string? Description,
    string SourceHash,
    Guid? InputSchemaId,
    Guid? OutputSchemaId,
    Guid? ConfigSchemaId) : IBaseDto;

/// <summary>
/// Read-side DTO returned to clients. Carries <c>Id</c> + 4 audit fields per HTTP-07
/// (Read DTOs include server-side fields). Implements <see cref="IHasId"/> so
/// <c>BaseController.Create</c> can read <c>read.Id</c> in <c>CreatedAtAction</c>.
/// Implements <see cref="IBaseDto"/> for narrative-field symmetry across Create/Update/Read.
/// 12 positional params: Id + 7 from CreateDto + 4 audit.
/// </summary>
public sealed record ProcessorReadDto(
    Guid Id,
    string Name,
    string Version,
    string? Description,
    string SourceHash,
    Guid? InputSchemaId,
    Guid? OutputSchemaId,
    Guid? ConfigSchemaId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy) : IBaseDto, IHasId;
