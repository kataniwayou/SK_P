using System.Net;
using System.Net.Sockets;
using BaseConsole.Core.DependencyInjection;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// The D-02 in-memory Generic-Host validation vehicle for the whole BaseConsole.Core library.
///
/// <para>
/// Unlike the API base library's <c>WebApplicationFactory&lt;Program&gt;</c> analog, this fixture boots a
/// real <see cref="Host.CreateApplicationBuilder()"/> (an <c>IHostApplicationBuilder</c>) and composes the
/// three-call seam exactly as a concrete console would:
/// <list type="number">
///   <item><c>builder.AddBaseConsoleObservability(cfg)</c> — metrics-only OTel, no traces (CONSOLE-02).</item>
///   <item><c>builder.Services.AddBaseConsole(cfg)</c> — soft-dep Redis + health + embedded listener (CONSOLE-01/03/05).</item>
///   <item><c>builder.Services.AddBaseConsoleMessaging(cfg, x =&gt; { })</c> — bus + correlation filters (CONSOLE-04).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Soft-dep boot resilience proof.</b> The Redis and RabbitMQ ports are DEAD/unreachable
/// (<c>127.0.0.1:6399,abortConnect=false</c> + an unreachable RabbitMQ host). The host must still build and
/// start without throwing — the embedded health listener answers <c>/health/live</c> while the deps are
/// down (CONSOLE-HEALTH-02 / T-18-09). <c>IBusControl</c> remains resolvable.
/// </para>
///
/// <para>
/// <b>Ephemeral health port (D-04).</b> The fixture picks a free TCP port at construction time rather than
/// the 8081 default, so parallel/sequential Console test classes never collide on the embedded listener
/// bind. <see cref="HealthPort"/> + <see cref="HttpClient"/> are exposed for the health-probe tests.
/// </para>
///
/// <para>
/// xUnit v3 <see cref="IAsyncLifetime"/>: <see cref="InitializeAsync"/> runs <c>Host.StartAsync()</c>
/// (flipping the startup gate via StartupCompletionService and starting the embedded listener);
/// <see cref="DisposeAsync"/> stops + disposes. <c>TestContext.Current.CancellationToken</c> is threaded
/// through the async calls (xUnit1051 under TreatWarningsAsErrors).
/// </para>
/// </summary>
public class ConsoleTestHostFixture : IAsyncLifetime
{
    // DEAD Redis port — soft-dep proves boot-safety; abortConnect=false keeps the multiplexer
    // from throwing at registration/connect time so the host can start with Redis unreachable.
    private const string DeadRedisConnectionString = "127.0.0.1:6399,abortConnect=false,connectTimeout=1000";

    public IHost Host { get; private set; } = null!;

    /// <summary>The ephemeral free port the embedded health listener was bound to.</summary>
    public int HealthPort { get; }

    /// <summary>HttpClient pointing at the embedded listener (http://127.0.0.1:{HealthPort}).</summary>
    public HttpClient HttpClient { get; }

    public ConsoleTestHostFixture()
    {
        HealthPort = FindFreeTcpPort();
        HttpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{HealthPort}") };

        // Fully-qualified: the `Host` property below shadows the Microsoft.Extensions.Hosting.Host type.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(BuildConfig(HealthPort));

        ConfigureBuilder(builder);

        Host = builder.Build();
    }

    /// <summary>
    /// Composes the three-call seam. Overridable so variant fixtures (e.g. the startup-gate 503 negative
    /// test) can mutate the service collection BEFORE <c>Build()</c>.
    /// </summary>
    protected virtual void ConfigureBuilder(IHostApplicationBuilder builder)
    {
        builder.AddBaseConsoleObservability(builder.Configuration);
        builder.Services.AddBaseConsole(builder.Configuration);
        builder.Services.AddBaseConsoleMessaging(builder.Configuration, x => { });  // empty consumer seam (D-06)
    }

    /// <summary>The in-memory configuration shared by all variants. The health port is parameterized.</summary>
    protected static Dictionary<string, string?> BuildConfig(int healthPort) => new()
    {
        ["Service:Name"]    = "orchestrator-test",
        ["Service:Version"] = "18.0.0",
        // DEAD Redis port — soft dep (CONSOLE-03 boot resilience).
        ["ConnectionStrings:Redis"] = DeadRedisConnectionString,
        // Unreachable RabbitMQ host — the bus is wired but never reaches a live broker this phase.
        ["RabbitMq:Host"]     = "localhost",
        ["RabbitMq:Username"] = "guest",
        ["RabbitMq:Password"] = "guest",
        // Ephemeral free port (D-04) — never the 8081 default, to avoid cross-test collisions.
        ["ConsoleHealth:Port"] = healthPort.ToString(),
        // The OTLP exporter does not connect at build/start time; the SDK default endpoint is fine.
    };

    /// <summary>
    /// Binds a TcpListener to port 0 (OS-assigned free port), reads the chosen port, releases it.
    /// The brief release-then-rebind window is acceptable for a single-process test host.
    /// </summary>
    private static int FindFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        // Starts StartupCompletionService (gate.MarkReady) + EmbeddedHealthEndpointService (inner Kestrel).
        // Must not throw despite dead Redis/RabbitMQ (soft-dep boot resilience).
        await Host.StartAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        HttpClient.Dispose();
        if (Host is not null)
        {
            var ct = TestContext.Current.CancellationToken;
            await Host.StopAsync(ct);
            Host.Dispose();
        }
    }
}
