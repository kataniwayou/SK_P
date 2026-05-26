using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using BaseApi.Core.Entities;

namespace BaseApi.Core.Persistence.Interceptors;

/// <summary>
/// Stamps audit fields (Id, CreatedAt, UpdatedAt, CreatedBy, UpdatedBy) on
/// <see cref="BaseEntity"/>-derived entries before SaveChangesAsync writes them.
///
/// <para>
/// Honors a caller-set non-empty <see cref="BaseEntity.Id"/> (per CONTEXT.md D-02) —
/// only generates a new Guid when Id == Guid.Empty.
/// </para>
///
/// <para>
/// <b>UTC-only:</b> all DateTime stamps are <see cref="DateTimeKind.Utc"/> because Npgsql 8
/// rejects non-UTC writes to <c>timestamptz</c> columns with <c>InvalidCastException</c>
/// (Pitfall 1). Time source is <see cref="TimeProvider"/> so tests can pin time via
/// <c>FakeTimeProvider</c> (Microsoft.Extensions.TimeProvider.Testing 8.10.0).
/// </para>
///
/// <para>
/// <b>Async-only:</b> overrides <see cref="SavingChangesAsync"/> only. Synchronous
/// <c>DbContext.SaveChanges()</c> will NOT trigger audit stamping — production code
/// must use the async save path.
/// </para>
///
/// <para>
/// <b>Null HttpContext is safe (D-08):</b> when invoked from a non-HTTP execution path
/// (background work, migrations, scratch console, unit tests without HttpContext),
/// <c>_httpContextAccessor.HttpContext</c> is null and <c>CreatedBy</c>/<c>UpdatedBy</c>
/// are stamped null with no exception, no warning log.
/// </para>
/// </summary>
public sealed class AuditInterceptor : SaveChangesInterceptor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TimeProvider _clock;

    public AuditInterceptor(IHttpContextAccessor httpContextAccessor, TimeProvider clock)
    {
        _httpContextAccessor = httpContextAccessor;
        _clock = clock;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is null)
        {
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        var now = _clock.GetUtcNow().UtcDateTime; // Kind == Utc by construction (Pitfall 1)
        var user = _httpContextAccessor.HttpContext?.User?.Identity?.Name; // null when no HttpContext

        foreach (EntityEntry<BaseEntity> entry in context.ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.Id == Guid.Empty)
                    {
                        entry.Entity.Id = Guid.NewGuid(); // D-01/D-02
                    }
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.CreatedBy = user;
                    entry.Entity.UpdatedBy = user;
                    break;

                case EntityState.Modified:
                    // Defensive: prevent caller from overwriting CreatedAt/CreatedBy via Update().
                    entry.Property(nameof(BaseEntity.CreatedAt)).IsModified = false;
                    entry.Property(nameof(BaseEntity.CreatedBy)).IsModified = false;
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = user;
                    break;
            }
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
