using BaseApi.Core.Entities;

namespace BaseApi.Service.Features.Workflow;

/// <summary>
/// Workflow domain entity — apex of the entity FK graph (no other entity references
/// it). ENTITY-08 (resolved per RESEARCH §5 Open Risk #1): the entity carries ONLY
/// the optional <c>CronExpression</c> scalar; the two M2M collections from the
/// requirement (entry-step collection + assignment collection) live on the DTOs
/// only and are persisted via the <c>WorkflowEntrySteps</c> + <c>WorkflowAssignments</c>
/// junction tables, synchronized by <c>WorkflowService.SyncJunctionsAsync</c>.
/// <para>
/// <c>CronExpression</c> is nullable — a null value means "Workflow not scheduled"
/// per ENTITY-08. Validation (VALID-19) parses non-null values via
/// <c>Cronos.CronExpression.Parse</c> with the default <c>CronFormat.Standard</c>
/// (5-field). 6-field expressions return 400 via the Phase 4 validation handler
/// (SC#4). The EF column type is the default snake_case string column with
/// <c>HasMaxLength(120)</c> upper bound (generous for any 5-field shape).
/// </para>
/// <para>
/// <b>Junction collections deliberately NOT on this entity</b> per RESEARCH §5 Open
/// Risk #1 + ENTITY-09 (no nav props between entities). The next-step / assignment
/// collections live on the DTOs only and are kept in sync via the two
/// <c>SyncJunctionsAsync</c> branches in <c>WorkflowService</c>. The junction
/// lifecycle is owned by this principal Workflow — both junction configurations use
/// <c>OnDelete(Cascade)</c> on the Workflow-side FK so DELETE Workflow auto-removes
/// the junction rows; the referenced-entity-side FK uses <c>Restrict</c> so deleting
/// a Step or Assignment that a Workflow points to returns 23503 → 422 (SC#5 for
/// the Step side, ERROR-11 surface for both).
/// </para>
/// </summary>
public sealed class WorkflowEntity : BaseEntity
{
    public string? CronExpression { get; set; }
}
