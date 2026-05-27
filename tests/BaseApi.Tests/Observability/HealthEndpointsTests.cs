using System.Net;
using BaseApi.Core.Health;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// SC#3 (HEALTH-01 / HEALTH-02 / HEALTH-03 / HEALTH-05):
/// <list type="bullet">
///   <item><c>/health/live</c> returns 200 even when Postgres is unreachable (no DB tag).</item>
///   <item><c>/health/ready</c> returns 503 when Postgres is unreachable; 200 when reachable.</item>
///   <item><c>/health/startup</c> returns 200 by default in Phase 5 (StartupCompletionService flips
///         the gate); 503 if we construct a factory where the gate is never flipped
///         (achieved by removing the IHostedService registration by type identity).</item>
/// </list>
/// Plus SC#4 logs-half: <c>/health/*</c> requests produce NO OTLP log records (filtered by
/// MEL <c>Microsoft.AspNetCore: Warning</c> setting — request-start/finish logs suppressed).
/// Plus T-05-READY-DB-EXPOSE: ready body contains per-check status but NO secrets/stack traces.
/// </summary>
[Collection("Observability")]
public sealed class HealthEndpointsTests
{
    [Fact]
    public async Task Test_HealthLive_Always_200_NoDbCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new HealthDeadPostgresFixture();   // Postgres unreachable in this factory
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Healthy\"", body);
    }

    [Fact]
    public async Task Test_HealthReady_503_When_Postgres_Unreachable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new HealthDeadPostgresFixture();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Unhealthy\"", body);
    }

    [Fact]
    public async Task Test_HealthReady_200_When_Postgres_Reachable()
    {
        var ct = TestContext.Current.CancellationToken;
        // Planner-checker WARNING #1: env-var-after-construct does NOT override the IConfiguration
        // snapshot WebApplicationFactory<Program> captures at first host build. Use the nested
        // HealthLiveLocalhostFixture which overrides ConnectionStrings:Postgres via
        // ConfigureAppConfiguration BEFORE the host is built.
        await using var factory = new HealthLiveLocalhostFixture();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Healthy\"", body);
    }

    [Fact]
    public async Task Test_HealthStartup_200_After_GateFlipped_By_HostedService()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new OtelCollectorFixture();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // Phase 5 default — StartupCompletionService (IHostedService) flips the gate during host start.
        // By the time CreateClient() returns, the host has finished StartAsync on all services.
        var response = await client.GetAsync("/health/startup", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Healthy\"", body);
        Assert.Contains("\"startup\"", body);
    }

    [Fact]
    public async Task Test_HealthStartup_503_Before_GateFlipped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new HealthNoStartupCompletionFixture();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/startup", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Unhealthy\"", body);
    }

    [Fact]
    public async Task Test_HealthReady_Body_Has_Per_Check_Status_But_No_Sensitive_Fields()
    {
        var ct = TestContext.Current.CancellationToken;
        // Planner-checker WARNING #1: use HealthLiveLocalhostFixture (ConfigureAppConfiguration)
        // so the IConfiguration ConnectionStrings:Postgres is actually localhost:5433 at host build time.
        await using var factory = new HealthLiveLocalhostFixture();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // SHAPE: per-check status surfaced
        Assert.Contains("\"entries\":{", body);
        Assert.Contains("\"startup\":", body);
        Assert.Contains("\"npgsql\":", body);

        // T-05-READY-DB-EXPOSE: secrets MUST NOT appear in body
        Assert.DoesNotContain("Password=", body);
        Assert.DoesNotContain("password=", body);
        Assert.DoesNotContain("postgres;Username", body);
        Assert.DoesNotContain("at Npgsql.", body);  // stack-trace marker
    }

    [Fact]
    public async Task Test_HealthEndpoints_Absent_From_OTLP_Logs()
    {
        var ct = TestContext.Current.CancellationToken;
        // This test's assertions are path-string-only - it only checks that "/health/live",
        // "/health/ready", and "/health/startup" do NOT appear in any exported OTLP log
        // record. Whether the underlying NpgSql health check returns Healthy or Unhealthy is
        // irrelevant; even if /health/ready returns 503 due to Postgres unreachable in the
        // default Host=postgres config, the path-string filter assertion is still what is
        // being verified. NO Postgres reachability dependency.
        //
        // DEVIATION FROM PLAN (Rule 1 — bug): the original plan assumed
        // `Microsoft.AspNetCore=Warning` from appsettings.json would suppress request-start
        // logs for /health/* (since /health/* should not be filtered at the path level —
        // CONTEXT D-09 deferred per-path filtering, so the coarse category filter is the
        // mechanism). WebApplicationFactory<Program> defaults to ASPNETCORE_ENVIRONMENT=
        // Development, which loads appsettings.Development.json — that file raises
        // Microsoft.AspNetCore back to Information, so request-start logs for /health/* DO
        // reach OTLP under the default test environment. Fix: use the OtelCollectorFixture's
        // logLevelDefaultOverride knob to set both Default AND Microsoft.AspNetCore down to
        // Warning, replicating the production behavior the test is meant to verify.
        //
        // RACE-CONDITION GUARD: when this test runs as part of the full HealthEndpointsTests
        // class, prior tests may have written records to telemetry.jsonl that arrive in the
        // file AFTER this fixture's InitializeAsync position-marker (the Collector batches
        // writes; records produced by tests N-1 may flush during test N's window). To stop
        // those records from polluting our assertion, we (a) wait briefly BEFORE
        // InitializeAsync so the Collector has time to drain prior tests' buffered records,
        // and (b) take the position marker AFTER that drain.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        await using var factory = new HealthFilterEnabledFixture();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();
        // Issue 10 probe requests to swamp the export stream IF the filter is broken.
        // Status codes are intentionally ignored - see comment above.
        for (int i = 0; i < 10; i++)
        {
            _ = await client.GetAsync("/health/live", ct);
            _ = await client.GetAsync("/health/ready", ct);
            _ = await client.GetAsync("/health/startup", ct);
        }
        await factory.FlushAsync(TimeSpan.FromSeconds(1));

        var logs = factory.ReadExportedLogs();
        // For each log record's body / scope attributes, assert no "/health/" path appears.
        var rawJoined = string.Concat(logs.Select(l => l.GetRawText()));
        Assert.DoesNotContain("/health/live", rawJoined);
        Assert.DoesNotContain("/health/ready", rawJoined);
        Assert.DoesNotContain("/health/startup", rawJoined);
    }

    // -------- Specialized fixtures for HealthEndpointsTests -----------------------------------

    /// <summary>
    /// Variant: replaces appsettings connection string with a dead-port one BEFORE Program.cs
    /// reads <c>cfg.GetConnectionString("Postgres")</c> for the <c>.AddNpgSql(...)</c> health
    /// check registration. Used by SC#3 negative-path tests (/health/ready -> 503 when Postgres
    /// unreachable).
    ///
    /// <para>
    /// DEVIATION FROM PLAN (Rule 1 — bug): the original plan used
    /// <c>ConfigureAppConfiguration + AddInMemoryCollection</c> on the IWebHostBuilder, but
    /// that callback runs DURING <c>builder.Build()</c> — AFTER Program.cs's
    /// <c>builder.Services.AddNpgSql(cfg.GetConnectionString("Postgres")!, ...)</c> has
    /// already CAPTURED the connection string by value into the registered IHealthCheck. The
    /// ConfigureAppConfiguration override does take effect on the IConfiguration object, but
    /// it's too late to influence the already-captured connection string. Verified empirically:
    /// when ConfigureAppConfiguration set the dead-port string, <c>cfg.GetConnectionString</c>
    /// returned the dead-port string post-build, yet <c>/health/ready</c> still returned 200
    /// because the NpgSqlHealthCheck still held the original appsettings value.
    /// </para>
    ///
    /// <para>
    /// CORRECT pattern: set <c>Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", ...)</c>
    /// IN the fixture's constructor (BEFORE the base WebApplicationFactory&lt;Program&gt; ctor
    /// runs, BEFORE any code in Program.cs executes). The env-var source is one of the default
    /// configuration sources loaded by <c>WebApplication.CreateBuilder</c>, so Program.cs's
    /// <c>cfg.GetConnectionString("Postgres")</c> reads our override directly. The planner-checker
    /// WARNING #1 was correct about "AFTER fixture construction" being broken; setting BEFORE
    /// construction (in the ctor body) is the working pattern.
    /// </para>
    /// </summary>
    private sealed class HealthDeadPostgresFixture : OtelCollectorFixture
    {
        private readonly string? _priorEnvValue;
        public HealthDeadPostgresFixture()
        {
            // Set env var in ctor — runs BEFORE any base ctor logic that builds the host.
            // Capture+restore the prior value on dispose so subsequent fixtures see the
            // pristine appsettings-derived connection string (process-wide env vars persist).
            _priorEnvValue = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
            Environment.SetEnvironmentVariable("ConnectionStrings__Postgres",
                "Host=localhost;Port=1;Database=postgres;Username=postgres;Password=postgres;Timeout=2");
        }
        public override async ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue);
            await base.DisposeAsync();
        }
    }

    /// <summary>
    /// Variant: replaces appsettings connection string with the LIVE localhost:5433 dev
    /// connection string via the same env-var-in-ctor pattern documented on
    /// <see cref="HealthDeadPostgresFixture"/>. Used by SC#3 positive-path tests
    /// (/health/ready -> 200 when Postgres reachable on localhost:5433) AND the
    /// T-05-READY-DB-EXPOSE ready-body shape test.
    /// </summary>
    private sealed class HealthLiveLocalhostFixture : OtelCollectorFixture
    {
        private readonly string? _priorEnvValue;
        public HealthLiveLocalhostFixture()
        {
            _priorEnvValue = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
            Environment.SetEnvironmentVariable("ConnectionStrings__Postgres",
                "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres");
        }
        public override async ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue);
            await base.DisposeAsync();
        }
    }

    /// <summary>
    /// Variant: sets Microsoft.AspNetCore log level to Warning to replicate production-mode
    /// log filtering behavior in the Development test environment. Required because
    /// appsettings.Development.json raises Microsoft.AspNetCore to Information for dev
    /// ergonomics, but the SC#4 logs-half assertion specifically tests the production-mode
    /// invariant (request-start/finish logs suppressed). LogLevel override is applied via
    /// ConfigureAppConfiguration which DOES work for log filters (the MEL filter reads the
    /// IConfiguration at every log event, unlike AddNpgSql which captures the connection
    /// string by value at registration time).
    /// </summary>
    private sealed class HealthFilterEnabledFixture : OtelCollectorFixture
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
                    ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Warning",
                    ["Logging:LogLevel:Microsoft.AspNetCore.Routing"] = "Warning",
                });
            });
        }
    }

    /// <summary>
    /// Variant: removes StartupCompletionService registration so IStartupGate stays Unhealthy.
    /// Used by SC#3 negative-path: /health/startup -> 503 before gate flipped.
    /// </summary>
    private sealed class HealthNoStartupCompletionFixture : OtelCollectorFixture
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // Planner-checker INFO #2: predicate matches the concrete IHostedService registration
                // by Type identity. Refactor-safe vs the previous string-name match — renaming the
                // class (or moving namespaces) would silently break the previous predicate.
                var toRemove = services
                    .Where(d => d.ImplementationType == typeof(StartupCompletionService))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);
            });
        }
    }
}
