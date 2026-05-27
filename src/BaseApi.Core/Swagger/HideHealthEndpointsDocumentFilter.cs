using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BaseApi.Core.Swagger;

/// <summary>
/// Removes any path starting with <c>/health</c> from the generated OpenAPI document.
/// Health endpoints are registered via <c>MapHealthChecks</c> (not <c>[ApiController]</c>
/// actions) and typically do not appear in ApiExplorer output — this filter is
/// defense-in-depth (CONTEXT D-18). Mirrors the
/// <c>AspNetCoreInstrumentationOptions.Filter</c> exclusion in Phase 5 traces.
/// </summary>
internal sealed class HideHealthEndpointsDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var pathsToRemove = swaggerDoc.Paths
            .Where(kv => kv.Key.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var path in pathsToRemove)
        {
            swaggerDoc.Paths.Remove(path);
        }
    }
}
