using BaseApi.Core.Persistence.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BaseApi.Core.Exceptions.Handlers;

/// <summary>
/// IExceptionHandler #3 in the D-06 chain — claims <see cref="DbUpdateException"/>
/// and ALL subtypes including <see cref="DbUpdateConcurrencyException"/>.
///
/// <para>
/// <b>CRITICAL ORDER (Pitfall 7 / D-09):</b> the concurrency subtype check
/// MUST come BEFORE <see cref="PostgresExceptionMapper.TryMap"/>.
/// <c>DbUpdateConcurrencyException</c> is an EF-layer detection (0 rows
/// affected because <c>xmin</c> advanced) — it has no Postgres SQLSTATE inner,
/// so the mapper would return <c>false</c> and the handler would fall through
/// to <c>FallbackExceptionHandler</c> 500 instead of the locked 409.
/// </para>
///
/// <para>
/// <b>Information-disclosure guard (T-04-XMIN / D-09):</b> the 409 response
/// detail is the generic message
/// <c>"The resource was modified by another request; reload and retry."</c>
/// The <c>xmin</c> value, the row Id, and the conflicting field set are NEVER
/// exposed — they are internal Postgres details.
/// </para>
///
/// <para>
/// <b>Pitfall 6 (fast-bail):</b> if the exception is not a
/// <see cref="DbUpdateException"/>, returns <c>false</c> IMMEDIATELY with no
/// side effects. If mapped SQLSTATE is unknown, also returns <c>false</c>
/// so <see cref="FallbackExceptionHandler"/> claims the 500.
/// </para>
/// </summary>
public sealed class DbUpdateExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;

    public DbUpdateExceptionHandler(IProblemDetailsService pdSvc) => _pdSvc = pdSvc;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateException due) return false;  // Pitfall 6

        // CRITICAL ORDER (Pitfall 7 / D-09): concurrency FIRST.
        if (exception is DbUpdateConcurrencyException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            var concurrencyProblem = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Detail = "The resource was modified by another request; reload and retry.",
            };
            return await _pdSvc.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = concurrencyProblem,
                Exception = exception,
            });
        }

        // Then attempt Postgres SQLSTATE mapping (D-08).
        if (PostgresExceptionMapper.TryMap(due, out var status, out var detail, out var col))
        {
            httpContext.Response.StatusCode = status;
            var problem = new ProblemDetails
            {
                Status = status,
                Title = status == StatusCodes.Status422UnprocessableEntity
                    ? "Unprocessable Entity" : "Conflict",
                Detail = detail,
            };
            if (col is not null) problem.Extensions["field"] = col;
            return await _pdSvc.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problem,
                Exception = exception,
            });
        }

        return false;  // unmapped SQLSTATE → FallbackExceptionHandler
    }
}
