using Asp.Versioning.ApiExplorer;
using BaseApi.Core.Middleware;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;                   // HostEnvironmentEnvExtensions.IsDevelopment(IHostEnvironment)

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Phase 7 middleware pipeline. CONTEXT D-19 locks the order:
/// <list type="number">
///   <item><c>UseExceptionHandler</c> FIRST so it wraps the rest (Phase 4 D-01).</item>
///   <item><c>CorrelationIdMiddleware</c> — stamps HttpContext.Items["CorrelationId"] (Phase 4).</item>
///   <item><c>UseRouting</c> — endpoint matching.</item>
///   <item>Dev-only: <c>UseSwagger</c> + <c>UseSwaggerUI</c> (HTTP-16 / SC#4).</item>
///   <item><c>MapHealthChecks</c> x3 — /health/live, /health/ready, /health/startup with tag predicates.</item>
/// </list>
/// <c>MapControllers</c> is called by Program.cs (NOT this extension) so tests can MapControllers
/// independently of UseBaseApi.
/// </summary>
public static class BaseApiApplicationBuilderExtensions
{
    public static WebApplication UseBaseApi(this WebApplication app)
    {
        app.UseExceptionHandler();                                       // FIRST (Phase 4 D-01)
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseRouting();
        // CORS deliberately omitted in v1 — no REQ-ID specifies a policy (CONTEXT D-19).

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(opts =>
            {
                foreach (var description in app.DescribeApiVersions())
                {
                    opts.SwaggerEndpoint(
                        $"/swagger/{description.GroupName}/swagger.json",
                        description.GroupName.ToUpperInvariant());
                }
            });
        }

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

        return app;
    }
}
