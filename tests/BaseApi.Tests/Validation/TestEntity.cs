using BaseApi.Core.Entities;

namespace BaseApi.Tests.Validation;

/// <summary>
/// Test entity for Phase 6 SC#4 Mapperly scaffold.
/// Extends Phase 3 <see cref="BaseEntity"/> (8 fields) with one extra scalar
/// (<c>Note</c>) so the mapper has at least one non-base field to map — proves
/// the source-gen handles BOTH base + concrete fields.
///
/// <para>
/// Namespace deliberately differs from <c>BaseApi.Tests.Persistence.TestEntity</c>
/// (Phase 3) to avoid type-name collision — both classes coexist in the test
/// assembly.
/// </para>
/// </summary>
public sealed class TestEntity : BaseEntity
{
    public string Note { get; set; } = string.Empty;
}
