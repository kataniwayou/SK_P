using BaseApi.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BaseApi.Tests.Composition;

/// <summary>
/// BaseDbContext subclass scoped to Phase 7 verification. Tracks
/// <see cref="BaseApi.Tests.Validation.TestEntity"/> — the SAME TestEntity Plan 07-02 uses
/// for SC#2 ordering proof (NOT the <c>BaseApi.Tests.Persistence.TestEntity</c> that
/// <see cref="BaseApi.Tests.Persistence.TestDbContext"/> tracks).
///
/// Inherits Phase 3's snake_case naming convention (BaseDbContext.OnConfiguring) and
/// xmin shadow-property iteration (BaseDbContext.OnModelCreating) without redeclaring
/// either — the verbatim Phase 3 contract.
///
/// Why a NEW class instead of reusing TestDbContext: TestDbContext's namespace import for
/// TestEntity points at <c>BaseApi.Tests.Persistence.TestEntity</c> (Phase 3's persistence-scoped
/// shape). Plan 07-02 facts use <c>BaseApi.Tests.Validation.TestEntity</c> (Phase 6's
/// validation-scoped shape with the <c>Note</c> field) because that's the entity the Phase 6
/// scaffolds (TestDtos, TestEntityMapper, TestDtoValidator) already wire. Adding a sibling
/// DbContext is cleaner than fighting cross-namespace ambiguity.
/// </summary>
public sealed class Phase7TestDbContext : BaseDbContext
{
    public Phase7TestDbContext(DbContextOptions<Phase7TestDbContext> options) : base(options) { }

    public DbSet<BaseApi.Tests.Validation.TestEntity> TestEntities
        => Set<BaseApi.Tests.Validation.TestEntity>();
}
