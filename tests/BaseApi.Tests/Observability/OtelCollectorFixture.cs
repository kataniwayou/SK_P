using System.Text.Json;
using BaseApi.Tests.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Fused fixture: <see cref="WebApplicationFactory{TEntryPoint}"/> for in-process Kestrel
/// + <see cref="IAsyncLifetime"/> for the <c>tests/.otel-out/telemetry.jsonl</c> truncate-on-init
/// / delete-on-dispose discipline (CONTEXT.md D-11; lifts Phase 3/4 PostgresFixture cadence).
///
/// <para>
/// <b>Constructor (Reconciliation 4b):</b> optional <c>string?</c> connectionString param —
/// when supplied, registers a <see cref="TestErrorDbContext"/> wired against the per-class
/// Postgres throwaway DB (used by <c>TraceExportTests</c>). Tests passing <c>null</c> skip
/// DB wiring (used by <c>LogExportTests</c>, <c>LogLevelFilterTests</c>, <c>MetricsExportTests</c>).
/// </para>
///
/// <para>
/// <b>OTel test invariant (D-11):</b> <c>ExportProcessorType.Simple</c> is set on the OTLP
/// exporter via <see cref="IServiceCollection.Configure{T}(IServiceCollection, System.Action{T})"/>
/// on <c>OtlpExporterOptions</c>. This bypasses the default 5-second Batch processor so the
/// file exporter receives records synchronously — tests can read the file after
/// <see cref="FlushAsync"/>.
/// </para>
///
/// <para>
/// <b>OTLP endpoint pinning (T-05-OTLP-EXFIL mitigation):</b> the fixture pins the exporter
/// endpoint to <c>http://localhost:4317</c> via <c>OtlpExporterOptions.Endpoint</c> override
/// AND sets the env var defensively. Any test producing telemetry destined for a different
/// host would NOT appear in <c>tests/.otel-out/telemetry.jsonl</c>.
/// </para>
/// </summary>
public sealed class OtelCollectorFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Absolute path to the JSON-lines file the Collector writes to.</summary>
    public static readonly string TelemetryFile = ResolveTelemetryFile();

    private readonly string? _connectionString;
    private readonly string? _logLevelDefaultOverride;

    /// <summary>
    /// PUBLIC parameterless constructor — xUnit's IClassFixture activation requires the
    /// fixture type to define exactly ONE public constructor (multiple public ctors trigger
    /// "may only define a single public constructor"; ctors with default parameters do NOT
    /// satisfy IClassFixture's parameter-resolution either — they raise
    /// "had one or more unresolved constructor arguments"). Solution: a parameterless public
    /// ctor for IClassFixture activation + internal-overload constructors below for the
    /// test classes that need an injected connection string or log-level override
    /// (LogLevelFilterTests, HealthEndpointsTests, TraceExportTests all <c>new</c> the
    /// fixture directly inside test methods + nested subclasses chain through these
    /// internal ctors).
    /// </summary>
    public OtelCollectorFixture() : this(null, null) { }

    /// <summary>
    /// Internal overload used by tests that directly <c>new</c> a fixture with a Postgres
    /// connection string (TraceExportTests). Reached via the chain
    /// <c>: this(connectionString, null)</c>.
    /// </summary>
    internal OtelCollectorFixture(string? connectionString) : this(connectionString, null) { }

    /// <summary>
    /// Internal full constructor. NOTE (process-wide side effect — intentional, planner-checker
    /// INFO #3): sets the <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable for the test
    /// process to <c>http://localhost:4317</c>. This persists for the lifetime of the test
    /// process (the variable is NOT cleared on <see cref="DisposeAsync"/>). Intentional in
    /// Phase 5 because every test class wants the same Collector endpoint pin
    /// (T-05-OTLP-EXFIL). FLAGGED FOR PHASE 6+: if a future test verifies env-var-not-set
    /// behavior, refactor to a scoped helper that captures + restores the prior value.
    /// </summary>
    internal OtelCollectorFixture(string? connectionString, string? logLevelDefaultOverride)
    {
        _connectionString = connectionString;
        _logLevelDefaultOverride = logLevelDefaultOverride;

        // T-05-OTLP-EXFIL defensive — pin env var even before ConfigureWebHost runs.
        // Persistent process-wide side effect — see ctor XML doc above for rationale.
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
    }

    public ValueTask InitializeAsync()
    {
        var dir = Path.GetDirectoryName(TelemetryFile)!;
        Directory.CreateDirectory(dir);
        // POSITION-MARKER strategy (NOT truncate, NOT delete). Surfaced during Plan 05-02:
        // the otel-collector v0.95.0 file exporter keeps an open write handle on
        // telemetry.jsonl for the container's lifetime. If we truncate (SetLength(0))
        // it MAY work on some platforms, but if we delete the file while the container
        // is alive, the exporter writes to the now-orphaned inode and a new directory
        // entry is never created — silently breaking ALL subsequent fixture instances.
        // Safe alternative: record the file's length AT InitializeAsync, then
        // ReadAllExportedRecords seeks past that offset so we only see records emitted
        // during THIS test's lifetime. The .gitignore convention keeps the working tree
        // clean across the file's accumulated bytes; D-11 cleanup discipline is honored
        // at the verifier-snapshot level (Task 8 reports file absent post-test by
        // verifying it can be cleaned only AFTER `docker compose down` brings the
        // collector down — see SUMMARY for details).
        if (File.Exists(TelemetryFile))
        {
            try { _startPosition = new FileInfo(TelemetryFile).Length; }
            catch { _startPosition = 0; }
        }
        else
        {
            _startPosition = 0;
        }
        return ValueTask.CompletedTask;
    }

    private long _startPosition;

    /// <summary>
    /// Async disposal — the single override below satisfies BOTH the
    /// <see cref="IAsyncDisposable"/> contract (used by callers via <c>await using</c>)
    /// AND the <see cref="IAsyncLifetime.DisposeAsync"/> contract (xUnit v3 unifies them on
    /// the same signature — same method, no separate adapter). Planner-checker WARNING #2:
    /// hiding (with the <c>new</c> keyword) + a synchronous <c>base.Dispose()</c> would break
    /// virtual dispatch AND skip the async release of Kestrel + the IHost.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        // D-11 cleanup discipline — INTENTIONALLY NOT deleting telemetry.jsonl here.
        // The otel-collector container's file exporter holds an exclusive write handle on
        // the file for the container's lifetime. Deleting the directory entry on the
        // host side orphans the inode the exporter is writing to — subsequent fixture
        // instances would find no new directory entry created and the test session
        // permanently sees an empty file. Cleanup is instead delegated to the workflow's
        // tear-down: `docker compose down` releases the handle so the file can be
        // removed by `rm tests/.otel-out/telemetry.jsonl` AFTER the collector exits.
        // The .gitignore convention (`tests/.otel-out/*` + `!tests/.otel-out/.gitkeep`)
        // keeps the working tree clean regardless of the file's accumulated bytes.
        // MUST be the async base disposal — sync base.Dispose() would skip async tear-down
        // of Kestrel + IHost services (planner-checker WARNING #2).
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_logLevelDefaultOverride is not null)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Default"] = _logLevelDefaultOverride,
                });
            });
        }

        builder.ConfigureTestServices(services =>
        {
            // Discover test assembly's TestObservabilityController via assembly part (Phase 4 pattern)
            services.AddControllers()
                .AddApplicationPart(typeof(OtelCollectorFixture).Assembly);

            // Force deterministic OTLP flush (CONTEXT D-11)
            services.Configure<OtlpExporterOptions>(o =>
            {
                o.ExportProcessorType = ExportProcessorType.Simple;
                o.Endpoint            = new Uri("http://localhost:4317");
            });

            if (_connectionString is not null)
            {
                services.AddDbContext<TestErrorDbContext>(opts =>
                    opts.UseNpgsql(_connectionString)
                        .UseSnakeCaseNamingConvention());
            }
        });
    }

    // ---- JSON-lines readers (CONTEXT D-11) ------------------------------------------

    public IReadOnlyList<JsonElement> ReadAllExportedRecords()
    {
        if (!File.Exists(TelemetryFile)) return Array.Empty<JsonElement>();
        // Read with full sharing so the Collector container's exclusive write handle
        // does not block us. Skip past _startPosition so we never see pre-test records
        // (set when InitializeAsync could not truncate due to container ownership).
        using var fs = new FileStream(TelemetryFile, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (_startPosition > 0 && _startPosition <= fs.Length)
        {
            fs.Seek(_startPosition, SeekOrigin.Begin);
        }
        using var reader = new StreamReader(fs);
        var result = new List<JsonElement>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Defensive: skip lines that don't parse — file rotation may produce truncated tails
            try
            {
                using var doc = JsonDocument.Parse(line);
                result.Add(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                // partial line during write — ignore
            }
        }
        return result;
    }

    public IReadOnlyList<JsonElement> ReadExportedLogs()
        => ReadAllExportedRecords().Where(e => e.TryGetProperty("resourceLogs", out _)).ToList();

    public IReadOnlyList<JsonElement> ReadExportedMetrics()
        => ReadAllExportedRecords().Where(e => e.TryGetProperty("resourceMetrics", out _)).ToList();

    public IReadOnlyList<JsonElement> ReadExportedTraces()
        => ReadAllExportedRecords().Where(e => e.TryGetProperty("resourceSpans", out _)).ToList();

    /// <summary>
    /// Force-flush all OTel provider trees (logs/metrics/traces) before reading.
    /// Even with ExportProcessorType.Simple set on OTLP exporter, the Collector takes a short
    /// moment to receive over gRPC + write the file. Tests call this then read.
    /// </summary>
    public async Task FlushAsync(TimeSpan? wait = null)
    {
        var meterProvider  = Services.GetService<MeterProvider>();
        var tracerProvider = Services.GetService<TracerProvider>();
        var loggerProvider = Services.GetService<LoggerProvider>();
        meterProvider?.ForceFlush(timeoutMilliseconds: 5_000);
        tracerProvider?.ForceFlush(timeoutMilliseconds: 5_000);
        loggerProvider?.ForceFlush(timeoutMilliseconds: 5_000);
        // Allow file-exporter rotation to settle
        await Task.Delay(wait ?? TimeSpan.FromMilliseconds(500));
    }

    private static string ResolveTelemetryFile()
    {
        // Walk up from test base dir until SK_P.sln is found, then append tests/.otel-out/telemetry.jsonl
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("SK_P.sln").Any())
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Cannot locate solution root (SK_P.sln) from test base dir");
        return Path.Combine(dir.FullName, "tests", ".otel-out", "telemetry.jsonl");
    }
}
