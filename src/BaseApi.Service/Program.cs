// BaseApi.Service — application entry point.
//
// Phase 1 scaffold per CONTEXT.md D-10. The host boots, registers controllers
// (none exist yet — Phase 8 adds the 5 concrete controllers), and runs. Every
// HTTP path returns 404 until later phases register routes.
//
// Phase 7 will replace the body with:
//   builder.Services.AddBaseApi<AppDbContext>(builder.Configuration);
//   app.UseBaseApi();
//   app.MapControllers();
// (See .planning/research/ARCHITECTURE.md Pattern 1 — Composition Root.)

using BaseApi.Core.Exceptions.Handlers;
using BaseApi.Core.Middleware;

var builder = WebApplication.CreateBuilder(args);

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
app.MapControllers();

app.Run();

// Marker type for WebApplicationFactory<Program> in Phase 8 integration tests.
// (Top-level statements generate an internal Program class by default; partial
// class declaration here promotes it to public so the test project can target it.)
public partial class Program { }
