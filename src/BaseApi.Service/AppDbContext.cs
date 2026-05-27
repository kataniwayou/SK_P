using BaseApi.Core.Persistence;
using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using Microsoft.EntityFrameworkCore;

namespace BaseApi.Service;

/// <summary>
/// Concrete DbContext for BaseApi.Service. Phase 8 Wave C 08-07 populates this with the
/// 5 entity DbSets (Schema/Processor/Step/Assignment/Workflow) + 3 junction DbSets
/// (StepNextSteps/WorkflowEntrySteps/WorkflowAssignments) and overrides
/// <c>OnModelCreating</c> with the load-bearing
/// <c>ApplyConfigurationsFromAssembly → base.OnModelCreating</c> ordering (CONTEXT D-10 +
/// RESEARCH Pitfall 6) so the Phase 3 xmin shadow concurrency token iteration in
/// <c>BaseDbContext.OnModelCreating</c> sees all 5 fully-configured BaseEntity subclasses
/// and stamps each with the <c>xmin xid</c> column in the generated <c>InitialCreate</c>
/// migration (PERSIST-16).
/// </summary>
public sealed class AppDbContext : BaseDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // 5 entity DbSets (PERSIST-01)
    public DbSet<SchemaEntity> Schemas => Set<SchemaEntity>();
    public DbSet<ProcessorEntity> Processors => Set<ProcessorEntity>();
    public DbSet<StepEntity> Steps => Set<StepEntity>();
    public DbSet<AssignmentEntity> Assignments => Set<AssignmentEntity>();
    public DbSet<WorkflowEntity> Workflows => Set<WorkflowEntity>();

    // 3 junction DbSets (PERSIST-12)
    public DbSet<StepNextSteps> StepNextSteps => Set<StepNextSteps>();
    public DbSet<WorkflowEntrySteps> WorkflowEntrySteps => Set<WorkflowEntrySteps>();
    public DbSet<WorkflowAssignments> WorkflowAssignments => Set<WorkflowAssignments>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // CONTEXT D-10 + RESEARCH Pitfall 6: ApplyConfigurationsFromAssembly FIRST so each entity
        // is registered on the model; base.OnModelCreating LAST so Phase 3's xmin shadow-token
        // iteration (BaseDbContext.OnModelCreating) sees all 5 newly-configured BaseEntity subclasses.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
