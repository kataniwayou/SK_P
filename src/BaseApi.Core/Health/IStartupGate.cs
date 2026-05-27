namespace BaseApi.Core.Health;

/// <summary>
/// One-shot startup gate exposed via DI as a Singleton.
///
/// <para>
/// <b>Phase 5 default (D-01 / D-13):</b> <see cref="StartupCompletionService"/>
/// is registered as an <c>IHostedService</c> whose <c>StartAsync</c> calls
/// <see cref="MarkReady"/> — so <c>/health/startup</c> reports Healthy as soon as
/// the host completes initialization in v1 (no migrations yet).
/// </para>
///
/// <para>
/// <b>Phase 8 contract:</b> <see cref="StartupCompletionService"/> is REPLACED by a
/// <c>MigrationRunner : IHostedService</c> registered FIRST (so it runs ahead of
/// any future hosted services). The runner calls <c>db.Database.MigrateAsync()</c>
/// THEN <see cref="MarkReady"/>. Clean one-file substitution; no other Phase 5 code changes.
/// </para>
/// </summary>
public interface IStartupGate
{
    /// <summary>True once <see cref="MarkReady"/> has been called at least once.</summary>
    bool IsReady { get; }

    /// <summary>Idempotently transitions the gate to the ready state. Thread-safe.</summary>
    void MarkReady();
}

/// <summary>
/// Thread-safe one-shot latch backing <see cref="IStartupGate"/>.
///
/// <para>
/// Reads use <c>Volatile.Read</c> for cross-thread visibility; writes use
/// <c>Interlocked.Exchange</c> for atomicity. Idempotent: multiple
/// <see cref="MarkReady"/> calls are a no-op after the first.
/// </para>
///
/// <para>
/// <b>Access modifier (deviation from CONTEXT D-01 wording):</b> <c>public sealed</c>,
/// not <c>internal sealed</c>. <c>services.AddSingleton&lt;IStartupGate, StartupGate&gt;()</c>
/// in <c>BaseApi.Service.Program.cs</c> resolves <c>StartupGate</c> across the assembly
/// boundary; <c>internal</c> would require <c>InternalsVisibleTo</c>. PATTERNS.md
/// recommends <c>public sealed</c> for consistency with every other Core sealed type
/// (NotFoundExceptionHandler, FallbackExceptionHandler, CorrelationIdMiddleware, etc.).
/// </para>
/// </summary>
public sealed class StartupGate : IStartupGate
{
    private int _isReady; // 0 = false, 1 = true (Interlocked.Exchange has no bool overload in .NET 8)

    /// <inheritdoc/>
    public bool IsReady => Volatile.Read(ref _isReady) == 1;

    /// <inheritdoc/>
    public void MarkReady() => Interlocked.Exchange(ref _isReady, 1);
}
