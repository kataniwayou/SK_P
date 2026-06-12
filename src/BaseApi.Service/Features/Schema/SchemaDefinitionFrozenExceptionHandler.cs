using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BaseApi.Service.Features.Schema;

/// <summary>
/// Domain IExceptionHandler that claims <see cref="SchemaDefinitionFrozenException"/> →
/// HTTP 409 Conflict + RFC 7807 (CFG-10 / D-08). Mirrors <c>OrchestrationValidationExceptionHandler</c>
/// exactly, swapping the exception type + status code (422 → 409, the state-conflict framing from
/// <c>DbUpdateExceptionHandler</c>).
///
/// <para>
/// <b>Pitfall 6 (fast-bail):</b> if the exception is not a
/// <see cref="SchemaDefinitionFrozenException"/>, returns <c>false</c> IMMEDIATELY with no side
/// effects — the next handler (ultimately the split-out <c>FallbackExceptionHandler</c>) claims it.
/// This keeps the handler from claiming foreign exceptions (T-57-10).
/// </para>
///
/// <para>
/// <b>D-02:</b> the handler sets only Status/Title/Detail. It does NOT set <c>correlationId</c> or
/// <c>Instance</c> — the Phase 4 <c>CustomizeProblemDetails</c> customizer injects both on every
/// emission. The 409 body carries ONLY the schema Guid + a generic message (T-57-09 safe-disclosure).
/// </para>
/// </summary>
public sealed class SchemaDefinitionFrozenExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;

    public SchemaDefinitionFrozenExceptionHandler(IProblemDetailsService pdSvc) => _pdSvc = pdSvc;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not SchemaDefinitionFrozenException ex) return false;  // Pitfall 6: bail FAST

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title  = "Schema definition is frozen",
            Detail = $"Schema '{ex.SchemaId}' is referenced by a processor; its Definition cannot be modified. " +
                     "Create a new schema and re-point. (Name and Description remain editable.)",
        };  // correlationId + Instance injected by CustomizeProblemDetails — do NOT set here (D-02)

        return await _pdSvc.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
