namespace BaseApi.Core.Mapping;

/// <summary>
/// Generic 3-method mapping contract consumed by <c>BaseService&lt;...&gt;</c>
/// (Phase 7) and implemented per-entity by a Mapperly <c>[Mapper] partial class</c>
/// (Phase 8). Closes HTTP-10.
///
/// <para>
/// <list type="bullet">
///   <item><see cref="ToEntity"/>: build a NEW entity from the Create DTO; audit fields stamped by <c>AuditInterceptor</c> on <c>SaveChanges</c>.</item>
///   <item><see cref="Update"/>: MUTATE the existing target in place; EF Core change tracking + Phase 3 <c>xmin</c> shadow concurrency token detect conflicts (Phase 6 D-07).</item>
///   <item><see cref="ToRead"/>: project an entity to the Read DTO for HTTP responses; Read DTOs include server-side fields per HTTP-07.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Server-side field exclusion (Phase 6 D-08 amended 2026-05-27 — dual mechanism):</b>
/// (1) Source-side: <typeparamref name="TUpdate"/> DTOs do NOT expose <c>Id</c> or audit
/// fields (HTTP-06). Mapperly cannot map what isn't on the source.
/// (2) Target-side: each <c>[Mapper] partial class</c>'s <c>Update</c> method MUST declare
/// <c>[MapperIgnoreTarget(nameof(TEntity.Id))]</c> + four more for the audit fields
/// (Mapperly 4.x defaults <c>RequiredMappingStrategy=Both</c> — strict-mappings fires on
/// unmapped TARGET members; RMG012 promoted to Error via Directory.Build.props would
/// otherwise break the build).
/// </para>
/// </summary>
public interface IEntityMapper<TEntity, TCreate, TUpdate, TRead>
{
    TEntity ToEntity(TCreate dto);
    void    Update(TUpdate dto, TEntity target);
    TRead   ToRead(TEntity entity);
}
