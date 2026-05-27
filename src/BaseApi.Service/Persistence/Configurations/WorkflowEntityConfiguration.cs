using BaseApi.Service.Features.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BaseApi.Service.Persistence.Configurations;

/// <summary>
/// EF Core entity configuration for <see cref="WorkflowEntity"/>. Workflow is the apex of
/// the entity FK graph — no other entity references it — so it has no FK columns to
/// configure here. The two M2M relationships (entry-step + assignment) live entirely on
/// the junction-table configurations
/// (<c>WorkflowEntryStepsConfiguration</c> + <c>WorkflowAssignmentsConfiguration</c>);
/// ENTITY-09 (no nav props between entities) is preserved by avoiding
/// <c>HasMany</c>/<c>WithMany</c> declarations here.
/// <para>
/// <c>CronExpression</c> uses <c>HasMaxLength(120)</c> upper bound — generous for any
/// 5-field cron shape (e.g., <c>"*/15 0,12 * * 1-5"</c> &lt; 30 chars). The maxlength
/// is BOTH a defensive DoS guard at the persistence layer AND documentation that this
/// is not an unbounded text blob.
/// </para>
/// </summary>
internal sealed class WorkflowEntityConfiguration : IEntityTypeConfiguration<WorkflowEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowEntity> entity)
    {
        // CronExpression is nullable; no extra column config beyond default snake_case.
        // The next-step / assignment collections are NOT on the entity (junction-backed
        // per ENTITY-09); their configurations live in the junction-table EF configs.
        entity.Property(e => e.CronExpression)
            .HasMaxLength(120);  // generous upper bound for any cron shape
    }
}
