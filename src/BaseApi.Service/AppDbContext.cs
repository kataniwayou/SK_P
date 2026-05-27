using BaseApi.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BaseApi.Service;

/// <summary>
/// Concrete DbContext for BaseApi.Service. Phase 7 lands this as an empty placeholder so
/// Program.cs can collapse to the locked ~3-line declarative form (CONTEXT D-15). Phase 8
/// will populate it with the 5 entity DbSets (Schema/Processor/Step/Assignment/Workflow) +
/// 3 junction DbSets (StepNextSteps/WorkflowEntrySteps/WorkflowAssignments) + any
/// <c>OnModelCreating</c> overrides for FK constraints, unique indexes, jsonb columns,
/// junction PKs, etc. The empty class is safe because <see cref="BaseDbContext.OnModelCreating"/>
/// iterates <c>modelBuilder.Model.GetEntityTypes()</c> filtered by <c>typeof(BaseEntity).IsAssignableFrom</c>
/// (Phase 3 xmin shadow concurrency token wiring) — an empty type collection produces zero
/// side effects.
/// </summary>
public sealed class AppDbContext : BaseDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
}
