using BaseApi.Core.Entities;
using BaseApi.Core.Mapping;
using Riok.Mapperly.Abstractions;

namespace BaseApi.Tests.Validation;

/// <summary>
/// Phase 6 SC#4 scaffold — trivial Mapperly <c>[Mapper] partial class</c> implementing
/// <see cref="IEntityMapper{TEntity,TCreate,TUpdate,TRead}"/> for <see cref="TestEntity"/>.
/// Compile-success of this assembly under <c>Directory.Build.props</c>
/// <c>RMG007;RMG012;RMG020;RMG089</c> promotion (Plan 06-01) IS the SC#4 build-half proof.
///
/// <para>
/// <b>[MapperIgnoreTarget] / [MapperIgnoreSource] attributes (Phase 6 D-08 amended 2026-05-27 /
/// RESEARCH Pitfall 2 / Plan 06-02 fix-forward 2026-05-27):</b>
/// Mapperly 4.x defaults <c>RequiredMappingStrategy = Both</c> — strict-mappings fires on UNMAPPED
/// TARGET members AND UNMAPPED SOURCE members regardless of DTO shape. Plan 06-01's
/// <c>&lt;WarningsAsErrors&gt;</c> promotion turns RMG012 (target unmapped) and RMG020 (source
/// unmapped) into build errors. The 14 attributes below suppress each violation explicitly,
/// preserving drift detection: if a NEW property is added to TestEntity, RMG012/RMG020 still
/// fires unless that property is wired through the DTOs or added to the ignore list. Phase 8's
/// 5 entity mappers MUST replicate this exact attribute pattern across the 3 methods.
/// </para>
///
/// <para>
/// <b>Three attribute sites:</b>
/// <list type="bullet">
///   <item><c>ToEntity</c>: 5 [MapperIgnoreTarget] for the 5 server-side fields on TestEntity not present on TestCreateDto (Id + 4 audit).</item>
///   <item><c>Update</c>: 5 [MapperIgnoreTarget] for the 5 server-side fields on TestEntity not present on TestUpdateDto (Id + 4 audit) — Phase 6 D-08 amended.</item>
///   <item><c>ToRead</c>: 4 [MapperIgnoreSource] for the 4 audit fields on TestEntity not present on TestReadDto. (TestReadDto carries Id but NOT audit fields per the plan DTO definition.)</item>
/// </list>
/// </para>
/// </summary>
[Mapper]
public sealed partial class TestEntityMapper :
    IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>
{
    [MapperIgnoreTarget(nameof(TestEntity.Id))]
    [MapperIgnoreTarget(nameof(TestEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(TestEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(TestEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(TestEntity.UpdatedBy))]
    public partial TestEntity ToEntity(TestCreateDto dto);

    [MapperIgnoreTarget(nameof(TestEntity.Id))]
    [MapperIgnoreTarget(nameof(TestEntity.CreatedAt))]
    [MapperIgnoreTarget(nameof(TestEntity.UpdatedAt))]
    [MapperIgnoreTarget(nameof(TestEntity.CreatedBy))]
    [MapperIgnoreTarget(nameof(TestEntity.UpdatedBy))]
    public partial void Update(TestUpdateDto dto, TestEntity target);

    [MapperIgnoreSource(nameof(TestEntity.CreatedAt))]
    [MapperIgnoreSource(nameof(TestEntity.UpdatedAt))]
    [MapperIgnoreSource(nameof(TestEntity.CreatedBy))]
    [MapperIgnoreSource(nameof(TestEntity.UpdatedBy))]
    public partial TestReadDto ToRead(TestEntity entity);
}
