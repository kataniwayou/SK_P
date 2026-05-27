using BaseApi.Core.Exceptions.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Phase 4 error wiring: AddProblemDetails customizer that injects <c>correlationId</c> and
/// <c>instance</c> into every ProblemDetails emission (ERROR-08/09 / Phase 4 D-04), plus the
/// 4 IExceptionHandler chain in LOAD-BEARING order (Phase 4 D-06):
/// NotFound -> Validation -> DbUpdate (concurrency-FIRST per Phase 4 Pitfall 7) -> Fallback (catch-all).
/// </summary>
internal static class ErrorHandlingServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
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

        // ORDER LOAD-BEARING (Phase 4 D-06) — walked top-to-bottom; first to return true claims.
        services.AddExceptionHandler<NotFoundExceptionHandler>();
        services.AddExceptionHandler<ValidationExceptionHandler>();
        services.AddExceptionHandler<DbUpdateExceptionHandler>();
        services.AddExceptionHandler<FallbackExceptionHandler>();

        return services;
    }
}
