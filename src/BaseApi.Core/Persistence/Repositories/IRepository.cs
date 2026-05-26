using BaseApi.Core.Entities;

namespace BaseApi.Core.Persistence.Repositories;

/// <summary>
/// Generic repository over an audit-stamped <see cref="BaseEntity"/> subtype.
///
/// <para>
/// EXACTLY 5 methods (Phase 3 CONTEXT.md D-04). No <c>IQueryable&lt;T&gt;</c> leakage,
/// no <c>ExistsAsync</c> helper, no <c>Where(predicate)</c> overload. Junction entities
/// (StepNextSteps, WorkflowEntrySteps, WorkflowAssignments — Phase 8) do NOT derive
/// BaseEntity and are accessed via raw <c>DbContext.Set&lt;TJunction&gt;()</c> from the
/// entity-specific Service in Phase 8.
/// </para>
///
/// <para>
/// <b>Unit of work (D-05):</b> the Service owns <c>SaveChangesAsync</c>. Repository
/// methods mutate the change tracker only — Add/Update/Delete stage changes; the Service
/// composes multi-entity transactions and calls SaveChangesAsync at the boundary.
/// </para>
/// </summary>
public interface IRepository<TEntity> where TEntity : BaseEntity
{
    /// <summary>Returns the entity by Id, or null if missing. Service throws NotFoundException (ERROR-06 / Phase 4).</summary>
    Task<TEntity?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns all rows. No paging in v1 (HTTP-17..19 are v2).</summary>
    Task<IReadOnlyList<TEntity>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Stages an Add on the change tracker. Caller (Service) calls SaveChangesAsync.</summary>
    Task AddAsync(TEntity entity, CancellationToken cancellationToken);

    /// <summary>Stages an Update on the change tracker. Caller (Service) calls SaveChangesAsync.</summary>
    /// <remarks>Sync — DbSet&lt;T&gt;.Update is sync (no I/O); the async-by-symmetry shape would be a lie.</remarks>
    void Update(TEntity entity);

    /// <summary>
    /// Load-then-remove: fetches by Id, then stages a Remove. Returns silently if missing
    /// (Service is responsible for the NotFound semantics). Preserves the D-03 xmin check
    /// because the load tracks the row's current xmin.
    /// </summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
