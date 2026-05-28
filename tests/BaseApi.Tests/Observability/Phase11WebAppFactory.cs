using BaseApi.Tests.Composition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Phase 11 <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// subclass for backend round-trip E2E tests + the migrated Log/LogLevel/Metrics facts.
/// Composes <see cref="Phase8WebAppFactory"/>'s per-class throwaway-Postgres-DB pattern
/// with three Phase-11-specific knobs:
///
/// <list type="bullet">
///   <item>
///     <b>(a) <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> env-var pin</b> — defensively set to
///     <c>http://localhost:4317</c> in the constructor (T-05-OTLP-EXFIL inheritance from
///     the retired Phase 5 fixture lines 87-95). Prevents accidental telemetry leakage to
///     an unexpected collector if a developer sets a sibling env var.
///   </item>
///   <item>
///     <b>(b) <see cref="PeriodicExportingMetricReaderOptions"/>.<c>ExportIntervalMilliseconds = 1_000</c>
///     override</b> — RESEARCH Pattern 4 + Pitfall 7. The OTel .NET SDK defaults to a
///     60-second metric export interval; under that default a 30-second E2E fact times out
///     with zero samples in Prom. Override to 1 second for E2E determinism. Production
///     posture stays at 60s (this is a test-only override).
///   </item>
///   <item>
///     <b>(c) Optional <c>Logging:LogLevel:Default</c> override constructor</b> — preserves
///     parity with the retired Phase 5 fixture's 2-arg overload so
///     <c>LogLevelFilterTests</c> (migrated in Plan 11-08b) has a clean substitute for the
///     prior 2-arg form.
///   </item>
/// </list>
///
/// <para>
/// Inheritance chain: <c>Phase11WebAppFactory</c> → <c>Phase8WebAppFactory</c> →
/// <c>WebAppFactory</c> → <c>WebApplicationFactory&lt;Program&gt;</c>. Each layer composes
/// the next: Phase8 owns Postgres + ConnectionString rewrite; WebAppFactory owns
/// AddApplicationPart for the test-assembly controllers; Phase11 owns the OTel test-only
/// overrides. RESEARCH Open Q2 recommends this composition over evolving the retired Phase 5
/// fixture in place — fewer mutations to Phase 5/8 assets; cleaner trait + filter posture.
/// </para>
/// </summary>
public class Phase11WebAppFactory : Phase8WebAppFactory
{
    private readonly string? _logLevelDefaultOverride;

    /// <summary>
    /// xUnit's <c>IClassFixture&lt;Phase11WebAppFactory&gt;</c> requires exactly ONE public
    /// constructor with parameter resolution; this parameterless ctor satisfies that contract.
    /// Internal overload below is for direct <c>new</c> from <c>LogLevelFilterTests</c>
    /// migration in Plan 11-08.
    /// </summary>
    public Phase11WebAppFactory() : this(null) { }

    internal Phase11WebAppFactory(string? logLevelDefaultOverride)
    {
        _logLevelDefaultOverride = logLevelDefaultOverride;
        // T-05-OTLP-EXFIL defensive — pin env var even before ConfigureWebHost runs.
        // Persistent process-wide side effect (NOT cleared on DisposeAsync — same posture
        // as the retired Phase 5 fixture; the var stays pinned for the test process lifetime).
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_logLevelDefaultOverride is not null)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Default"] = _logLevelDefaultOverride,
                });
            });
        }

        // Phase 8 base wiring — PostgresFixture connection-string + AddApplicationPart.
        base.ConfigureWebHost(builder);

        // RESEARCH Pattern 4 / Pitfall 7 — override the SDK metric export interval from
        // the 60-second default to 1 second so E2E facts can observe exported metrics
        // within a bounded poll budget. Without this override, Plan 11-07/11-08 metric
        // tests time out at 30s with zero samples.
        builder.ConfigureTestServices(services =>
        {
            services.Configure<PeriodicExportingMetricReaderOptions>(opts =>
            {
                opts.ExportIntervalMilliseconds = 1_000;
            });

            // Discover the test assembly's TestObservabilityController via AddApplicationPart.
            // (Note: WebAppFactory base already does this for typeof(WebAppFactory).Assembly;
            // this repeats the call for typeof(Phase11WebAppFactory).Assembly which is the
            // same assembly. AddApplicationPart is idempotent — safe to repeat.)
            services.AddControllers()
                .AddApplicationPart(typeof(Phase11WebAppFactory).Assembly);
        });
    }
}
