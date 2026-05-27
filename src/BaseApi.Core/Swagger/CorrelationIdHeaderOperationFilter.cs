using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BaseApi.Core.Swagger;

/// <summary>
/// Documents <c>X-Correlation-Id</c> as an optional header parameter on every operation.
/// Phase 4 OBSERV-09/10/11 — server generates the value if absent and echoes it on the
/// response header. <c>MaxLength=128</c> aligns with the Phase 4
/// <see cref="BaseApi.Core.Middleware.CorrelationIdMiddleware"/> ASCII-printable guard.
/// </summary>
internal sealed class CorrelationIdHeaderOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters ??= new List<OpenApiParameter>();
        operation.Parameters.Add(new OpenApiParameter
        {
            Name        = "X-Correlation-Id",
            In          = ParameterLocation.Header,
            Required    = false,
            Description = "Optional correlation ID for request tracking. If absent, the server " +
                          "generates a new 32-char hex value and echoes it on the response header.",
            Schema      = new OpenApiSchema { Type = "string", MaxLength = 128 },
        });
    }
}
