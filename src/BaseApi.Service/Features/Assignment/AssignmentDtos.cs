using BaseApi.Core.Contracts;
using BaseApi.Core.Validation;

namespace BaseApi.Service.Features.Assignment;

/// <summary>
/// Create-side DTO. Excludes server-controlled fields (Id, CreatedAt, UpdatedAt,
/// CreatedBy, UpdatedBy) per HTTP-05; Mapperly cannot map what isn't on the source.
/// 5 positional params: Name, Version, Description, StepId, Payload.
/// </summary>
public sealed record AssignmentCreateDto(
    string Name,
    string Version,
    string? Description,
    Guid StepId,
    string Payload) : IBaseDto;

/// <summary>
/// Update-side DTO. Excludes server-controlled fields per HTTP-06. The <c>Update</c>
/// mapper method declares <c>[MapperIgnoreTarget]</c> for the 5 entity-side fields
/// not present here.
/// 5 positional params: Name, Version, Description, StepId, Payload.
/// </summary>
public sealed record AssignmentUpdateDto(
    string Name,
    string Version,
    string? Description,
    Guid StepId,
    string Payload) : IBaseDto;

/// <summary>
/// Read-side DTO returned to clients. Carries <c>Id</c> + 4 audit fields per HTTP-07.
/// Implements <see cref="IHasId"/> so <c>BaseController.Create</c> can read
/// <c>read.Id</c> in <c>CreatedAtAction</c>. Implements <see cref="IBaseDto"/> for
/// narrative-field symmetry across Create/Update/Read.
/// 10 positional params: Id + 5 + 4 audit fields.
/// </summary>
public sealed record AssignmentReadDto(
    Guid Id,
    string Name,
    string Version,
    string? Description,
    Guid StepId,
    string Payload,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy) : IBaseDto, IHasId;
