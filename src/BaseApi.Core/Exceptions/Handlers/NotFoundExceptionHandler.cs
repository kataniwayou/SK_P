using BaseApi.Core.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BaseApi.Core.Exceptions.Handlers;

/// <summary>
/// IExceptionHandler #1 in the D-06 chain — claims <see cref="NotFoundException"/>
/// → HTTP 404 with <c>resourceType</c> + <c>resourceId</c> extensions (D-07 + ERROR-06).
///
/// <para>
/// <b>Pitfall 6 (fast-bail):</b> if the exception is not a
/// <see cref="NotFoundException"/>, returns <c>false</c> IMMEDIATELY with no
/// side effects (no logging, no response writes) — the next handler in the
/// chain claims it.
/// </para>
/// </summary>
public sealed class NotFoundExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;

    public NotFoundExceptionHandler(IProblemDetailsService pdSvc) => _pdSvc = pdSvc;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not NotFoundException nfx) return false;  // Pitfall 6: bail FAST

        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = "Not Found",
            Detail = nfx.Message,
            Extensions =
            {
                ["resourceType"] = nfx.ResourceType,
                ["resourceId"] = nfx.Id,
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
