using System.Net;
using System.Net.Sockets;
using BaseApi.Core.Health;
using BaseApi.Tests.Composition;
using BaseApi.Tests.Observability.Helpers;
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
        await using var factory = new Phase11WebAppFactory();
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
        // This test's assertions are path-string-only — it only checks that "/health/live",
        // "/health/ready", and "/health/startup" do NOT appear in any exported OTLP log
        // record. Whether the underlying NpgSql health check returns Healthy or Unhealthy is
        // irrelevant; even if /health/ready returns 503, the path-string negation is still
        // what is being verified. NO Postgres reachability dependency.
        //
        // PHASE 11 D-16 MIGRATION (Plan 11-08a): was Phase 5 file-exporter + position-marker
        // readback against telemetry.jsonl; now polls Elasticsearch via ElasticsearchTestClient
        // and asserts NO log doc contains `/health/` substrings within an 8s budget. Negative
        // assertion budget is shorter than positive (30s in LogExportTests) — long enough for
        // ES indexing pipeline to flush any actual hit, short enough to keep suite wall-clock
        // manageable (RESEARCH PATTERNS option a + Plan 11-08b LogLevelFilterTests precedent).
        //
        // RACE-CONDITION GUARD: the 1-second pre-wait before fixture init lets the Collector
        // drain prior-test buffered records before our probe loop starts. Carries forward from
        // Phase 5 fix-forward.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        await using var factory = new HealthFilterEnabledFixture();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // IN-01 review fix: removed unused X-Probe-Batch-Id header. The prior version
        // attached a unique GUID per probe cycle and advertised it as a positive-control
        // sentinel, but no query/assertion ever consumed it — the only ES query below
        // searches for "/health/" substrings, never for the batch id. Removing the
        // unused header keeps the test smaller; future hardening can layer a positive
        // control by also querying for "test-obs ok ran" body text on a parallel probe.

        // Issue 10 probe requests to swamp the export stream IF the filter is broken.
        // Status codes are intentionally ignored — see comment above.
        for (int i = 0; i < 10; i++)
        {
            _ = await client.GetAsync("/health/live", ct);
            _ = await client.GetAsync("/health/ready", ct);
            _ = await client.GetAsync("/health/startup", ct);
        }

        // Poll ES with a regex query against the body / scope / attributes for any `/health/`
        // substring. Short budget (8s) for negative assertion — RESEARCH PATTERNS option a.
        using var es = new ElasticsearchTestClient();

        // Use a query_string query that matches ANY doc containing the literal `/health/`
        // substring in any indexed text field. The query body shape is field-shape-agnostic
        // (works for both `mapping.mode: none` (raw OTLP) and `mapping.mode: otel` (normalized)
        // outputs since query_string searches all _source by default).
        var queryBody = """
          {
            "size": 10,
            "query": { "query_string": { "query": "\"/health/\"" } }
          }
          """;

        var hit = await es.PollEsForLog(queryBody, timeoutMs: 8_000, ct: ct);
        Assert.Null(hit);
    }

    [Fact]
    public async Task HealthLive_200_When_Redis_Unreachable()  // INFRA-REDIS-06 + TEST-REDIS-05
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new HealthDeadRedisFixture();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_200_When_Redis_Unreachable()  // INFRA-REDIS-06 + TEST-REDIS-05
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new HealthDeadRedisFixture();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_Live_Returns_200_When_Broker_Dead()  // TEST-RMQ-03
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new HealthDeadRabbitFixture();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_Ready_Returns_200_When_Broker_Dead()  // TEST-RMQ-03
    {
        var ct = TestContext.Current.CancellationToken;
        // WebApi soft-on-broker posture (Phase 19 D-05): the MassTransit bus check is capped at
        // Degraded via MinimalFailureStatus (MessagingServiceCollectionExtensions.cs:49-54), so a
        // broker-down condition never flips /health/ready to 503. Postgres + Redis stay live.
        await using var factory = new HealthDeadRabbitFixture();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // T-20-04 (inherited T-19): the broker-down ready body must not leak secrets. Assert only
        // the documented secret-free contract (HealthEndpointsTests.cs:113-134) — never connection
        // strings or passwords.
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("Password=", body);
        Assert.DoesNotContain("password=", body);
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
    private sealed class HealthDeadPostgresFixture : Phase8WebAppFactory
    {
        // Dead-port conn string the NpgSql health check should fail against.
        private const string DeadConnectionString =
            "Host=localhost;Port=1;Database=postgres;Username=postgres;Password=postgres;Timeout=2";
        private readonly string? _priorEnvValue;
        // WR-04 review fix: skip the real PostgresFixture testcontainer boot — these
        // tests explicitly want Postgres UNREACHABLE, so paying ~10s per fixture instance
        // for a container that will never serve a connection is pure dev-loop waste
        // (~40s saved across the 4 facts that consume this fixture).
        public HealthDeadPostgresFixture()
            : base(skipPostgresFixture: true, connectionStringOverride: DeadConnectionString)
        {
            // Set env var in ctor — runs BEFORE any base ctor logic that builds the host.
            // Capture+restore the prior value on dispose so subsequent fixtures see the
            // pristine appsettings-derived connection string (process-wide env vars persist).
            // WR-02 review fix: wrap SetEnvironmentVariable in try/catch so a synchronous
            // failure inside SetEnvironmentVariable itself (rare — argument validation,
            // process-exit) does NOT leak the dead string.
            //
            // WR-A residual scoping (review re-pass): the try/catch covers only throws
            // inside SetEnvironmentVariable. The C# 8+ `await using` path on the fixture
            // DOES call DisposeAsync on a thrown InitializeAsync() — so the restore runs
            // for the standard caller pattern used throughout this file. BUT:
            //   (1) CALLERS MUST USE `await using` — a non-`await using` caller whose
            //       InitializeAsync throws will silently skip the restore.
            //   (2) This fixture is NOT safe to NEST inside another env-var-mutating
            //       fixture that targets the same `ConnectionStrings__Postgres` key —
            //       the inner would capture the outer's mutation as its "prior" baseline.
            //       The [Collection("Observability")] serialization currently prevents
            //       nesting; if that invariant changes, factor out an explicit
            //       EnvVarScope IDisposable helper instead of the inline pattern below.
            _priorEnvValue = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
            try
            {
                Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", DeadConnectionString);
            }
            catch
            {
                Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue);
                throw;
            }
        }
        // IN-11 review fix: ConfigureWebHost override removed. The pre-WR-04 world
        // required overriding base's AddInMemoryCollection because base would otherwise
        // write its own (throwaway-DB) connection string and OVERWRITE the env-var
        // dead-port value. After WR-04, base.ConfigureWebHost writes
        // `ConnectionString` (which is `_connectionStringOverride` = DeadConnectionString
        // here) — so the override block was writing DeadConnectionString a SECOND time
        // (harmless but stale + redundant). The base now does the right thing.
        public override async ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue);
            await base.DisposeAsync();
        }
    }

    /// <summary>
    /// Phase 12 TEST-REDIS-05 + D-13: dead-Redis variant proving /health/live AND
    /// /health/ready BOTH return 200 when Redis is unreachable. Soft-dep contract
    /// INFRA-REDIS-06; Phase 5 HEALTH-01..05 contracts byte-immutable per D-06.
    ///
    /// <para>
    /// Dead-Redis port = host's 6379 (D-13 — guaranteed unbound by Plan 12-02's
    /// compose 6380:6379 mapping). Live Postgres is kept (skipPostgresFixture=false)
    /// so /health/ready can pass on the Postgres-only ready-tag side.
    /// </para>
    ///
    /// <para>
    /// <b>Pitfall 3 defense:</b> a developer running a host-side Redis daemon on the
    /// default 6379 would make this fixture pass for the WRONG reason (their Redis
    /// happens to be DOWN at test time, not because compose maps 6380:6379). The
    /// ctor pre-flight TCP-connects to localhost:6379 and throws on bound port,
    /// surfacing the D-01 regression loudly.
    /// </para>
    /// </summary>
    private sealed class HealthDeadRedisFixture : Phase8WebAppFactory
    {
        // D-13 — 6379 is unbound on host since compose maps 6380:6379. connectTimeout=2000
        // caps the boot-side wait so the factory boots quickly even with Redis unreachable.
        private const string DeadRedisConnectionString =
            "localhost:6379,abortConnect=false,connectTimeout=2000";

        public HealthDeadRedisFixture()
            : base(
                skipPostgresFixture: false,
                connectionStringOverride: null!,
                skipRedisFixture: true,
                redisConnectionStringOverride: DeadRedisConnectionString)
        {
            AssertDeadRedisPortIsUnbound();
        }

        /// <summary>
        /// RESEARCH Pitfall 3 mitigation — fail loudly if a developer's host-side Redis
        /// is bound on 6379 (would make the dead-port test pass for the wrong reason).
        /// </summary>
        private static void AssertDeadRedisPortIsUnbound()
        {
            using var client = new TcpClient();
            try
            {
                // Synchronous, short-timeout probe (~200ms). Connection should fail
                // with SocketError.ConnectionRefused — if it SUCCEEDS, a host-side
                // Redis is bound on 6379 and the dead-port assumption is broken.
                var task = client.ConnectAsync("localhost", 6379);
                if (task.Wait(TimeSpan.FromMilliseconds(500)) && client.Connected)
                {
                    throw new InvalidOperationException(
                        "compose D-01 regression detected: host port 6379 is bound. " +
                        "Plan 12-02 should map redis to 6380:6379 so the host's 6379 " +
                        "remains unbound — defending the HealthDeadRedisFixture dead-port " +
                        "assumption (RESEARCH Pitfall 3). Refusing to run dead-Redis " +
                        "acceptance fact against a live Redis daemon on 6379.");
                }
            }
            catch (AggregateException ae) when (
                ae.InnerException is SocketException se &&
                se.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // Expected — port is unbound. Continue.
            }
            catch (SocketException se) when (se.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // Expected — port is unbound. Continue.
            }
        }
    }

    /// <summary>
    /// Phase 20 TEST-RMQ-03 / D-03: broker-down variant proving the WebApi soft-on-broker posture
    /// — <c>/health/live</c> AND <c>/health/ready</c> BOTH return 200 when RabbitMQ is unreachable.
    /// Mirrors <see cref="HealthDeadRedisFixture"/>: keeps Postgres + Redis LIVE
    /// (<c>skipRedisFixture:false</c>) and points ONLY the broker at a dead host via the
    /// env-var-in-ctor pattern documented on <see cref="HealthDeadPostgresFixture"/>.
    ///
    /// <para>
    /// <c>RabbitMq__Host</c> is set BEFORE the base <c>WebApplicationFactory&lt;Program&gt;</c> ctor
    /// runs (env-var configuration source is read by Program.cs's <c>AddBaseApiMessaging</c> at
    /// registration). Captured+restored in <see cref="DisposeAsync"/>; the
    /// <c>SetEnvironmentVariable</c> is wrapped in try/catch that restores on throw. The
    /// <c>[Collection("Observability")]</c> serialization prevents env-var nesting races
    /// (T-20-05).
    /// </para>
    ///
    /// <para>
    /// Why <c>/health/ready</c> stays 200: the auto-registered MassTransit bus check is capped at
    /// <c>Degraded</c> via <c>MinimalFailureStatus</c>
    /// (<c>MessagingServiceCollectionExtensions.cs:49-54</c>) — a broker-down condition never flips
    /// ready to 503 (Phase 19 D-05). A dead-host name (not a dead port) makes the bus connection
    /// attempt fail at name resolution.
    /// </para>
    /// </summary>
    private sealed class HealthDeadRabbitFixture : Phase8WebAppFactory
    {
        private readonly string? _prior;

        public HealthDeadRabbitFixture()  // live Postgres + live Redis kept; only the broker is dead
        {
            _prior = Environment.GetEnvironmentVariable("RabbitMq__Host");
            try
            {
                Environment.SetEnvironmentVariable("RabbitMq__Host", "localhost-rabbit-dead.invalid");
            }
            catch
            {
                Environment.SetEnvironmentVariable("RabbitMq__Host", _prior);
                throw;
            }
        }

        public override async ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable("RabbitMq__Host", _prior);
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
    private sealed class HealthLiveLocalhostFixture : Phase8WebAppFactory
    {
        private readonly string? _priorEnvValue;
        public HealthLiveLocalhostFixture()
        {
            // WR-02 review fix: same try/catch restore-on-SetEnvironmentVariable-throw
            // pattern as HealthDeadPostgresFixture. See WR-A scoping notes there:
            // restore on InitializeAsync-throw depends on caller `await using` discipline;
            // fixture is NOT nesting-safe across multiple env-var-mutating fixtures
            // targeting the same key.
            _priorEnvValue = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
            try
            {
                Environment.SetEnvironmentVariable("ConnectionStrings__Postgres",
                    "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres");
            }
            catch
            {
                Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", _priorEnvValue);
                throw;
            }
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
    private sealed class HealthFilterEnabledFixture : Phase11WebAppFactory
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
    private sealed class HealthNoStartupCompletionFixture : Phase8WebAppFactory
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
