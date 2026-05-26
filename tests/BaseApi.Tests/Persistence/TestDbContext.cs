using Microsoft.EntityFrameworkCore;
using BaseApi.Core.Persistence;

namespace BaseApi.Tests.Persistence;

/// <summary>
/// Concrete DbContext that derives <see cref="BaseDbContext"/> and adds the
/// single <see cref="TestEntity"/> DbSet. Inherits Plan 03-01's snake_case
/// convention (OnConfiguring) and xmin shadow-property iteration
/// (OnModelCreating) without redeclaring them — the verbatim Phase 3 contract.
/// </summary>
public sealed class TestDbContext : BaseDbContext
{
    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }

    public DbSet<TestEntity> TestEntities => Set<TestEntity>();
}
