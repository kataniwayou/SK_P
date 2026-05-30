using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BaseConsole.Core.Health;

/// <summary>
/// Embedded minimal-Kestrel <see cref="IHostedService"/> that serves the three Kubernetes-style
/// health probes — <c>/health/live</c>, <c>/health/ready</c>, <c>/health/startup</c> — on
/// <c>ConsoleHealth:Port</c> (default 8081), independent of the message bus (CONSOLE-HEALTH-01).
///
/// <para>
/// <b>Two-container design (Open-Q 1).</b> This listener builds its OWN minimal-Kestrel DI container,
/// separate from the outer Generic-Host container. Only two things cross the boundary: the outer
/// <see cref="IStartupGate"/> singleton instance (shared by reference so <c>/health/startup</c> tracks
/// the host's real startup latch) and a <see cref="BusReadyHealthCheck"/> constructed with the outer
/// <see cref="IServiceProvider"/> (so <c>/health/ready</c> reads the real bus state).
/// </para>
///
/// <para>
/// <b>Three-way probe split (D-05).</b>
/// <list type="bullet">
///   <item><c>/health/live</c> = the always-Healthy <c>"self"</c> check ONLY — never Redis/RMQ, so a
///   dependency blip can never flip liveness and trigger a pod restart (CONSOLE-HEALTH-02 / T-18-09).</item>
///   <item><c>/health/startup</c> = the <c>StartupHealthCheck</c> over the shared gate (CONSOLE-HEALTH-04).</item>
///   <item><c>/health/ready</c> = the <c>BusReadyHealthCheck</c> reading the outer bus (CONSOLE-HEALTH-03).</item>
/// </list>
/// </para>
///
/// <para>
/// The listener starts independently of the bus — <c>/health/live</c> answers while the bus is still
/// connecting. All three responses use the API-side JSON body writer for a uniform per-check payload
/// that carries status only (no connection strings / stack traces — T-18-08).
/// </para>
/// </summary>
internal sealed class EmbeddedHealthEndpointService : IHostedService
{
    private readonly IStartupGate _gate;
    private readonly IConfiguration _cfg;
    private readonly IServiceProvider _outer;
    private readonly ILogger<EmbeddedHealthEndpointService> _logger;
    private WebApplication? _app;

    public EmbeddedHealthEndpointService(
        IStartupGate gate,
        IConfiguration cfg,
        IServiceProvider outer,
        ILogger<EmbeddedHealthEndpointService> logger)
    {
        _gate = gate;
        _cfg = cfg;
        _outer = outer;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();

        // D-04: default 8081, test-overridable via ConsoleHealth:Port.
        var port = _cfg.GetValue<int?>("ConsoleHealth:Port") ?? 8081;
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        // Share the OUTER gate instance into the inner DI so /health/startup tracks the real latch.
        builder.Services.AddSingleton(_gate);
        // Bridge to the OUTER bus health so /health/ready reflects real bus state (Open-Q 1).
        builder.Services.AddSingleton(new BusReadyHealthCheck(_outer));

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })       // live = self-only
            .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup" })                // startup = host gate
            .AddCheck<BusReadyHealthCheck>("bus-ready", tags: new[] { "ready" });              // ready = bus state

        _app = builder.Build();

        _app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains("live"),
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
        });
        _app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains("ready"),
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
        });
        _app.MapHealthChecks("/health/startup", new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains("startup"),
            ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
        });

        // Independent of the bus — /health/live answers while the bus connects.
        // Failure isolation (WR-02): a bind failure (e.g. ConsoleHealth:Port already in use)
        // otherwise propagates out of Host.StartAsync and aborts the whole console process.
        // Log the actionable cause before rethrowing so the bind conflict is diagnosable.
        try
        {
            await _app.StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Embedded health listener failed to start on port {Port}. " +
                "Check ConsoleHealth:Port for a bind conflict.",
                port);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
