using Microsoft.EntityFrameworkCore;
using BaseApi.Core.Entities;

namespace BaseApi.Core.Persistence;

/// <summary>
/// Abstract base for all DbContexts in the BaseApi.Core ecosystem.
///
/// <para>
/// Concrete contexts (e.g., <c>AppDbContext</c> in Phase 8, <c>TestDbContext</c> in
/// Phase 3 tests) derive from this and add <c>DbSet&lt;TEntity&gt;</c> properties.
/// This base has NO DbSets — it provides three concerns: snake_case naming,
/// audit interception, and xmin concurrency tokens on every <see cref="BaseEntity"/>
/// subclass.
/// </para>
///
/// <para>
/// <b>Defense-in-depth wiring:</b> OnConfiguring duplicates the
/// <c>UseSnakeCaseNamingConvention()</c> call that Phase 7's <c>AddBaseApi&lt;TDbContext&gt;</c>
/// extension also performs. The duplication ensures test paths (which build DbContextOptions
/// directly without AddBaseApi) still get the correct configuration. The interceptor is
/// wired by the composition root (Phase 7) or the test fixture (Phase 3) via
/// DbContextOptionsBuilder.AddInterceptors(...), NOT here.
/// </para>
///
/// <para>
/// <b>Snake_case timing (Pitfall 4):</b> the convention is applied here so it is active
/// BEFORE the first migration is ever generated. Phase 8 ships the <c>InitialCreate</c>
/// migration; this base must already be in place at that point.
/// </para>
/// </summary>
public abstract class BaseDbContext : DbContext
{
    protected BaseDbContext(DbContextOptions options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseSnakeCaseNamingConvention();
        // AuditInterceptor is wired via AddInterceptors(...) at composition root (Phase 7)
        // OR by the test fixture's options builder (Phase 3). NOT here.
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // xmin shadow concurrency token on every BaseEntity subclass (D-03 / PERSIST-16).
        // Junction entities are excluded naturally because they do NOT derive BaseEntity.
        // Pitfall 6 verbatim — xmin is Postgres's xid system column; HasColumnName/HasColumnType
        // pin the mapping and ValueGeneratedOnAddOrUpdate tells EF that Postgres maintains it.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
            .Where(t => typeof(BaseEntity).IsAssignableFrom(t.ClrType)))
        {
            modelBuilder.Entity(entityType.ClrType)
                .Property<uint>("xmin")
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        }
    }
}
