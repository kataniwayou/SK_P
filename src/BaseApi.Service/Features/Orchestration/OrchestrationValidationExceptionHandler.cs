using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BaseApi.Service.Features.Orchestration;

/// <summary>
/// Domain IExceptionHandler that claims <see cref="OrchestrationValidationException"/> →
/// HTTP 422 + RFC 7807 (D-02 / D-03). Mirrors <c>NotFoundExceptionHandler</c> exactly,
/// swapping the exception type + status code.
///
/// <para>
/// <b>Pitfall 6 (fast-bail):</b> if the exception is not an
/// <see cref="OrchestrationValidationException"/>, returns <c>false</c> IMMEDIATELY with no
/// side effects — the next handler (ultimately the split-out <c>FallbackExceptionHandler</c>)
/// claims it. This keeps the handler from claiming foreign exceptions (T-14-03).
/// </para>
///
/// <para>
/// <b>D-02:</b> the handler sets only Status/Title/Detail/errors. It does NOT set
/// <c>correlationId</c> or <c>Instance</c> — the Phase 4 <c>CustomizeProblemDetails</c>
/// customizer injects both on every emission.
/// </para>
/// </summary>
public sealed class OrchestrationValidationExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;

    public OrchestrationValidationExceptionHandler(IProblemDetailsService pdSvc) => _pdSvc = pdSvc;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not OrchestrationValidationException ex) return false;  // Pitfall 6: bail FAST

        httpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = ex.Title,
            Detail = ex.Message,
            Extensions =
            {
                ["errors"] = ex.ErrorsExtension,
            },
        };

        return await _pdSvc.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
