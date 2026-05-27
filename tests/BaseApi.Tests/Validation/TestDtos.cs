using BaseApi.Core.Validation;

namespace BaseApi.Tests.Validation;

/// <summary>
/// Phase 6 SC#3 + SC#4 DTOs. All three implement <see cref="IBaseDto"/> (Phase 6 D-03 symmetry).
/// Positional records auto-implement the get-only properties required by IBaseDto.
/// TestReadDto adds the server-side <c>Id</c> per HTTP-07 (Read DTOs include audit/Id).
/// TestUpdateDto deliberately does NOT include <c>Id</c> or audit fields per HTTP-06 +
/// Phase 6 D-08 source-side guard (Mapperly cannot map what isn't on the source).
/// </summary>
public sealed record TestCreateDto(string Name, string Version, string? Description, string Note) : IBaseDto;

public sealed record TestUpdateDto(string Name, string Version, string? Description, string Note) : IBaseDto;

public sealed record TestReadDto(Guid Id, string Name, string Version, string? Description, string Note) : IBaseDto;
