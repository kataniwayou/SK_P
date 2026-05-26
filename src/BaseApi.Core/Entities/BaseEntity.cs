namespace BaseApi.Core.Entities;

/// <summary>
/// Abstract base for all audit-stamped domain entities.
///
/// <para>
/// Concrete subclasses (SchemaEntity, ProcessorEntity, StepEntity, AssignmentEntity,
/// WorkflowEntity — Phase 8) inherit Id + audit fields. Junction entities
/// (StepNextSteps, WorkflowEntrySteps, WorkflowAssignments) deliberately do
/// NOT derive — they are non-BaseEntity per ARCHITECTURE.md and are excluded
/// from the xmin shadow-property iteration (BaseDbContext.OnModelCreating).
/// </para>
///
/// <para>
/// All server-controlled fields (Id, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy)
/// are stamped by <c>AuditInterceptor</c> on SaveChangesAsync. Production code
/// MUST NOT assign these manually; the interceptor honors a caller-set non-empty
/// Id (D-02) but production HTTP paths exclude Id from CreateDto per HTTP-05.
/// </para>
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <remarks>UTC by convention — set by AuditInterceptor; never assign manually. (Pitfall 1 — Npgsql 8 rejects non-UTC writes to timestamptz with InvalidCastException.)</remarks>
    public DateTime CreatedAt { get; set; }

    /// <remarks>UTC by convention — set by AuditInterceptor; never assign manually. (Pitfall 1 — Npgsql 8 rejects non-UTC writes to timestamptz with InvalidCastException.)</remarks>
    public DateTime UpdatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public string? UpdatedBy { get; set; }

    public string? Description { get; set; }
}
