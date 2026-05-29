using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BaseApi.Core.Exceptions.Handlers;

/// <summary>
/// IExceptionHandler #4 in the D-06 chain — the catch-all. Claims every
/// exception not caught by handlers 1-3 (NotFound, Validation, DbUpdate).
///
/// <para>
/// <b>Information-disclosure guard (T-04-LEAK / D-12 / ERROR-07):</b> the
/// 500 response body carries ONLY <c>title</c> / <c>status</c> / generic
/// <c>detail</c> plus the customizer's <c>correlationId</c> + <c>instance</c>.
/// The exception type, message, and full stack trace are NEVER in the response
/// body — they are logged via <c>ILogger.LogError(ex, ...)</c> which MEL
/// serializes with structured fields. Phase 5 OTel inherits the structure via
/// the same MEL pipeline.
/// </para>
/// </summary>
public sealed class FallbackExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;
    private readonly ILogger<FallbackExceptionHandler> _logger;

    public FallbackExceptionHandler(
        IProblemDetailsService pdSvc,
        ILogger<FallbackExceptionHandler> logger)
    {
        _pdSvc = pdSvc;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // D-12: log full exception+stack via MEL; body never carries it.
        _logger.LogError(exception, "Unhandled exception on {Path}", httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Internal Server Error",
            Detail = "An unexpected error occurred.",
        };

        // OBSERV-REDIS-03: when a faulting layer tags the exception with the offending
        // operation name (exception.Data["redisOp"] — e.g. "UpsertAsync"/"KeyExistsAsync"),
        // surface ONLY that op name in the 500 body. No connection string, message, or stack
        // is ever copied here (T-04-LEAK / T-15-13 — the op name is a fixed, non-sensitive
        // literal chosen by the caller). correlationId + instance are added by the Phase 4
        // customizer on emission.
        if (exception.Data["redisOp"] is string redisOp && redisOp.Length > 0)
        {
            problem.Extensions["redisOp"] = redisOp;
        }

        // Attempt to write; ignore the result — we have claimed this exception regardless.
        // If response has already started (headers committed), TryWriteAsync returns false
        // but we still return true so the chain does not re-throw.
        _ = await _pdSvc.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });

        return true;  // catch-all: always claimed
    }
}
