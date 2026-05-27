using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BaseApi.Core.Exceptions.Handlers;

/// <summary>
/// IExceptionHandler #2 in the D-06 chain — claims
/// <see cref="FluentValidation.ValidationException"/> → HTTP 400
/// <c>ValidationProblemDetails</c> with field-level <c>errors</c> map (D-10 + ERROR-03).
///
/// <para>
/// Ships defensively in Phase 4 even though FluentValidation validators are
/// not wired into request pipelines until Phase 6 — D-10 closure means Phase 6
/// just calls <c>AddValidatorsFromAssembly</c> and the mapping is already live.
/// </para>
///
/// <para>
/// <b>ERROR-10 (model-binding 400 shape parity):</b> the framework's default
/// <c>InvalidModelStateResponseFactory</c> rides on <c>AddProblemDetails</c>
/// registration (D-11) and emits <c>ValidationProblemDetails</c> through the
/// same <c>IProblemDetailsService</c> path — so this handler is the
/// FluentValidation half of the closure; model-binding 400 produces the same
/// shape automatically via the framework default factory.
/// </para>
///
/// <para>
/// <b>Pitfall 6 (fast-bail):</b> if the exception is not a
/// <see cref="FluentValidation.ValidationException"/>, returns <c>false</c>
/// IMMEDIATELY with no side effects.
/// </para>
/// </summary>
public sealed class ValidationExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _pdSvc;

    public ValidationExceptionHandler(IProblemDetailsService pdSvc) => _pdSvc = pdSvc;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ValidationException vex) return false;  // Pitfall 6

        var errors = vex.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).ToArray());

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        var problem = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failed",
        };

        return await _pdSvc.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
