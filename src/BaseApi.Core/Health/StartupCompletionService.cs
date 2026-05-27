using BaseApi.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BaseApi.Core.Health;

/// <summary>
/// Phase 8 D-15 swap target: applies the production migration set at startup and
/// flips <see cref="IStartupGate.MarkReady"/> only on success. Migration failure
/// is LOGGED at <c>Critical</c> and swallowed — readiness probe stays Unhealthy
/// but the process does NOT crash (PERSIST-10).
///
/// <para>
/// <b>Why <see cref="BaseDbContext"/>, not <c>AppDbContext</c>:</b> Phase 7 D-14
/// registers <see cref="BaseDbContext"/> as a Scoped alias for the concrete
/// <c>TDbContext</c> (<c>AppDbContext</c> in <c>BaseApi.Service</c>). Resolving the
/// alias here keeps <c>BaseApi.Core</c> free of Service-side references while
/// still applying the production migrations against the concrete context.
/// </para>
///
/// <para>
/// <b>Scope discipline (PERSIST-15):</b> AppDbContext is Scoped; this hosted
/// service runs at the root scope. <see cref="IServiceScopeFactory.CreateScope"/>
/// is REQUIRED — resolving a Scoped dependency directly from the root provider
/// throws <c>InvalidOperationException</c>.
/// </para>
///
/// <para>
/// <b>Failure semantics (D-15 + D-16 + PERSIST-10 + HEALTH-01):</b> the catch
/// block writes a Critical log entry to the MEL pipeline (console + OTel sink)
/// and DOES NOT rethrow (IHostedService.StartAsync throwing would crash the
/// host) and DOES NOT call <see cref="IStartupGate.MarkReady"/> (the startup
/// probe must remain Unhealthy so the orchestrator does not route traffic).
/// </para>
/// </summary>
public sealed class StartupCompletionService : IHostedService
{
    private readonly IStartupGate _gate;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StartupCompletionService> _logger;

    public StartupCompletionService(
        IStartupGate gate,
        IServiceScopeFactory scopeFactory,
        ILogger<StartupCompletionService> logger)
    {
        _gate = gate;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            // Phase 7 D-14 registers BaseDbContext as a Scoped alias for the concrete TDbContext
            // (AppDbContext in BaseApi.Service). Resolving BaseDbContext here keeps Core free of
            // Service-side references while still applying the production migration set.
            var db = scope.ServiceProvider.GetRequiredService<BaseDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
            _gate.MarkReady();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Database migration failed on startup; readiness probe will remain unhealthy.");
            // PERSIST-10: DO NOT rethrow — IHostedService.StartAsync throwing crashes the process.
            // HEALTH-01: DO NOT call _gate.MarkReady() — startup probe must remain Unhealthy.
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
