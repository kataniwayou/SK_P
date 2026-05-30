namespace BaseConsole.Core.Health;

/// <summary>
/// One-shot startup gate exposed via DI as a Singleton.
///
/// <para>
/// <b>Phase-5 shape (console):</b> <see cref="StartupCompletionService"/> is registered as an
/// <c>IHostedService</c> whose <c>StartAsync</c> calls <see cref="MarkReady"/> — so
/// <c>/health/startup</c> reports Healthy as soon as the host completes initialization. The
/// console has no database/migration step, so there is no migration-runner variant here.
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
/// <c>public sealed</c> so <c>services.AddSingleton&lt;IStartupGate, StartupGate&gt;()</c>
/// resolves <c>StartupGate</c> across the assembly boundary without <c>InternalsVisibleTo</c>.
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
