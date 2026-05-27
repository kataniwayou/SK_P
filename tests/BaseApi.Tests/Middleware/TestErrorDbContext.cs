using Microsoft.EntityFrameworkCore;
using BaseApi.Core.Persistence;

namespace BaseApi.Tests.Middleware;

/// <summary>
/// DbContext for Plan 04-02 SQLSTATE + concurrency tests. Adds
/// <see cref="TestParentEntity"/> + <see cref="TestChildEntity"/> to the
/// Phase 3 BaseDbContext convention set (snake_case + xmin shadow concurrency
/// token applied automatically per Phase 3 D-03 / D-05).
///
/// <para>
/// Configures the FK + UQ constraint names explicitly so the Option A regex
/// in <c>PostgresExceptionMapper</c> (Plan 04-01 Task 5) extracts the column
/// names cleanly:
///   <c>fk_testchild_parent_id</c> → <c>parent_id</c>
///   <c>uq_testchild_name</c> → <c>name</c>
/// </para>
/// </summary>
public sealed class TestErrorDbContext : BaseDbContext
{
    public TestErrorDbContext(DbContextOptions<TestErrorDbContext> options) : base(options) { }

    public DbSet<TestParentEntity> Parents => Set<TestParentEntity>();
    public DbSet<TestChildEntity> Children => Set<TestChildEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Phase 3 D-03 xmin shadow-property iteration MUST run first.
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TestChildEntity>(b =>
        {
            b.HasOne<TestParentEntity>()
                .WithMany()
                .HasForeignKey(c => c.ParentId)
                .HasConstraintName("fk_testchild_parent_id")
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(c => c.Name)
                .IsUnique()
                .HasDatabaseName("uq_testchild_name");
        });
    }
}
