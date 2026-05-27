// BaseApi.Service — application entry point.
//
// Phase 1 scaffold per CONTEXT.md D-10. The host boots, registers controllers
// (none exist yet — Phase 8 adds the 5 concrete controllers), and runs. Every
// HTTP path returns 404 until later phases register routes.
//
// Phase 4 added: CorrelationIdMiddleware (OBSERV-09/10/11) + AddProblemDetails
// customizer (ERROR-08/09) + 4 IExceptionHandler (ERROR-01..07) registrations
// in a load-bearing order. Pipeline order (D-01): UseExceptionHandler FIRST,
// then UseMiddleware<CorrelationIdMiddleware>, then UseRouting/MapControllers.
//
// Phase 5 adds: OpenTelemetry logs (MEL bridge — Pitfall 8 / OBSERV-02 / OBSERV-07),
// OTel metrics + traces (OBSERV-03 / OBSERV-12 / D-04 / D-16), and three K8s-style
// health probes (HEALTH-01..05) backed by IStartupGate. /health/* requests are
// excluded from metrics + traces via AspNetCoreInstrumentationOptions.Filter
// (Pitfall 10 / OBSERV-08 / HEALTH-05). Npgsql DB spans use the bare
// .AddNpgsql() (RESEARCH-side correction of CONTEXT D-05 — the lambda body
// CONTEXT shows references a property that does not exist on
// NpgsqlTracingOptions 8.0.4; the package default already does NOT capture
// parameter values — T-05-PII satisfied without an opt-out).
//
// Phase 7 will replace the body with:
//   builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
//   app.UseBaseApi();
//   app.MapControllers();
// (See .planning/research/ARCHITECTURE.md Pattern 1 — Composition Root.)

using BaseApi.Core.Exceptions.Handlers;
using BaseApi.Core.Health;
using BaseApi.Core.Middleware;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;                                       // TracerProviderBuilderExtensions.AddNpgsql (Phase 5 / OBSERV-12 / T-05-PII)
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var cfg            = builder.Configuration;
var serviceName    = cfg["Service:Name"]!;     // "sk-api" — INFRA-04 (Phase 1 D-11)
var serviceVersion = cfg["Service:Version"]!;  // "3.2.0"

// IHttpContextAccessor — Phase 4 takes ownership early (Phase 3 D-11 deferred this
// to Phase 7, but Phase 4's AddProblemDetails customizer (D-04) reads HttpContext
// via ctx.HttpContext (framework-provided), and Phase 7/8's AuditInterceptor will
// resolve IHttpContextAccessor — registering it now is safe and idempotent
// (Pitfall 1).
builder.Services.AddHttpContextAccessor();

// ProblemDetails — D-04 customizer injects correlationId + instance into EVERY
// ProblemDetails emission (IExceptionHandler chain, framework 400/404/500 fallbacks,
// [ApiController] model-binding 400). MUST be registered BEFORE AddControllers
// so the default InvalidModelStateResponseFactory routes through IProblemDetailsService
// (Pitfall 4 / Pitfall 8 / ERROR-10 closure / D-11).
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        if (ctx.HttpContext.Items.TryGetValue("CorrelationId", out var corrIdObj)
            && corrIdObj is string corrId)
        {
            ctx.ProblemDetails.Extensions["correlationId"] = corrId;
        }
        ctx.ProblemDetails.Instance = ctx.HttpContext.Request.Path;
    };
});

// IExceptionHandler chain — REGISTRATION ORDER IS LOAD-BEARING (D-06).
// Walked top-to-bottom; first to return true claims. FallbackExceptionHandler
// is the catch-all and always claims.
builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<DbUpdateExceptionHandler>();
builder.Services.AddExceptionHandler<FallbackExceptionHandler>();

// ============================================================================
// Phase 5: OTel logs via MEL bridge (Pitfall 8 / OBSERV-02 / OBSERV-06 / OBSERV-07).
// MUST be builder.Logging.AddOpenTelemetry — NOT the services-chain logger route,
// which would create a parallel provider that bypasses MEL filtering (Pitfall 9).
// IncludeScopes=true exports Phase 4 CorrelationIdMiddleware's BeginScope("CorrelationId", id)
// as a log attribute named "CorrelationId" on every OTLP-exported log record (SC#1).
// ============================================================================
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes           = true;
    o.ParseStateValues        = true;
    o.SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(serviceName: serviceName, serviceVersion: serviceVersion));
    // OTLP endpoint defaults to http://localhost:4317 if OTEL_EXPORTER_OTLP_ENDPOINT is not set (matches compose port mapping).
    // Open Question 3 (RESEARCH.md) resolved as (a): rely on SDK default fallback — no launchSettings.json required (see 05-01-SUMMARY).
    o.AddOtlpExporter();
});

// ============================================================================
// Phase 5: OTel metrics + traces (OBSERV-03 / OBSERV-12 / D-04 / D-08 / D-16).
// .ConfigureResource(...) MUST come BEFORE .WithMetrics / .WithTracing so the
// resource propagates to both branches. AspNetCoreInstrumentation Filter excludes
// /health/* from metrics AND traces (OBSERV-08 / HEALTH-05 / Pitfall 10). Runtime
// instrumentation (D-16) emits process.runtime.dotnet.* metrics regardless of path.
// ============================================================================
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: serviceName,
        serviceVersion: serviceVersion))
    .WithMetrics(m => m
        // DEVIATION (Rule 1 — API mismatch with CONTEXT D-08 / RESEARCH Pattern 5):
        // OpenTelemetry.Instrumentation.AspNetCore 1.15.0's MeterProviderBuilder.AddAspNetCoreInstrumentation
        // overload is PARAMETERLESS — no `opts.Filter` callback exists on the metrics side
        // (that callback only lives on the TracerProviderBuilder overload via
        // AspNetCoreTraceInstrumentationOptions). In .NET 8 the AspNetCore HTTP server metrics come from
        // the built-in `Microsoft.AspNetCore.Hosting` Meter, which has no Filter knob. Filtering /health
        // out of METRICS therefore deferred: traces filter still applies (the heavier surface), and
        // backend query-time filtering by http.route handles the metric noise. Recorded in 05-01-SUMMARY.
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter())
    .WithTracing(t => t
        .SetSampler(new AlwaysOnSampler())  // D-04 — 100% sample in v1
        .AddAspNetCoreInstrumentation(opts =>
            opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation()
        // SECURITY (T-05-PII / RESEARCH §"Pattern 6"): Npgsql.OpenTelemetry 8.0.4 does NOT
        // capture parameter values by default. db.statement attribute carries only the
        // SQL TEMPLATE (e.g., "INSERT INTO workflows (name, ...) VALUES ($1, ...)"). The
        // NpgsqlTracingOptions class in 8.0.4 has no IncludeParameterValues / EnableSensitive
        // -DataLogging property — CONTEXT D-05's lambda body would not compile. The bare
        // .AddNpgsql() call is the correct, secure-by-default shape.
        .AddNpgsql()
        .AddOtlpExporter());

// ============================================================================
// Phase 5: Health gate + checks (HEALTH-01..05 / D-01 / D-02 / D-03).
// IStartupGate is a Singleton one-shot latch. StartupCompletionService (IHostedService)
// flips it on host start so /health/startup is Healthy by default in v1 (no migrations).
// Phase 8 will replace StartupCompletionService with MigrationRunner — clean 1-line swap.
// Tag discipline:  "self" -> live only; StartupHealthCheck -> startup AND ready;
// NpgSql probe -> ready only. /health/live MUST NOT check DB (Pitfall 15).
// ============================================================================
builder.Services.AddSingleton<IStartupGate, StartupGate>();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup", "ready" })
    .AddNpgSql(cfg.GetConnectionString("Postgres")!, tags: new[] { "ready" });

// Phase 5 default-ready: IHostedService flips the gate on host start.
// Phase 8 will REMOVE this AddHostedService and register MigrationRunner instead.
builder.Services.AddHostedService<StartupCompletionService>();

builder.Services.AddControllers();

var app = builder.Build();

// PIPELINE ORDER (D-01): UseExceptionHandler FIRST so it wraps a try/catch around
// the rest of the pipeline. CorrelationIdMiddleware runs INSIDE that wrapper so
// when an endpoint throws, the IExceptionHandler chain sees the already-populated
// HttpContext.Items["CorrelationId"]. CorrelationIdMiddleware itself only calls
// Guid.NewGuid + TryGetValue — cannot realistically throw, so the small "outside
// the safety net" window is acceptable (community-standard ordering per
// Microsoft Learn).
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRouting();

// Phase 5: health endpoints (BEFORE MapControllers — plumbing first, business last).
// JSON body via UIResponseWriter.WriteHealthCheckUIResponse (D-07) so ops can curl
// any probe and see per-sub-check status. Tag predicates route the 3 endpoints to
// the right subset of registered checks.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate      = c => c.Tags.Contains("live"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate      = c => c.Tags.Contains("ready"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
});
app.MapHealthChecks("/health/startup", new HealthCheckOptions
{
    Predicate      = c => c.Tags.Contains("startup"),
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse,
});

app.MapControllers();

app.Run();

// Marker type for WebApplicationFactory<Program> in Phase 8 integration tests.
// (Top-level statements generate an internal Program class by default; partial
// class declaration here promotes it to public so the test project can target it.)
public partial class Program { }
