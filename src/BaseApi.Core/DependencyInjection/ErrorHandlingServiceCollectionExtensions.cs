using BaseApi.Core.Exceptions.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Phase 4 error wiring: AddProblemDetails customizer that injects <c>correlationId</c> and
/// <c>instance</c> into every ProblemDetails emission (ERROR-08/09 / Phase 4 D-04), plus the
/// IExceptionHandler chain in LOAD-BEARING order (Phase 4 D-06):
/// NotFound -> Validation -> DbUpdate (concurrency-FIRST per Phase 4 Pitfall 7).
/// <para>
/// <b>Phase 14 D-04 (split-Fallback):</b> the catch-all <c>FallbackExceptionHandler</c> is
/// NO LONGER registered here. It is registered separately, LAST, by the composition root via
/// <see cref="AddBaseApiFallbackHandler"/> — called after <c>AddBaseApi</c> AND after
/// <c>AddAppFeatures</c> so domain handlers (e.g. <c>OrchestrationValidationExceptionHandler</c>)
/// register ahead of the catch-all. Walk order == registration order; first handler to return
/// <c>true</c> claims.
/// </para>
/// </summary>
public static class ErrorHandlingServiceCollectionExtensions
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
        // Fallback is split out (Phase 14 D-04) and registered LAST by AddBaseApiFallbackHandler.
        services.AddExceptionHandler<NotFoundExceptionHandler>();
        services.AddExceptionHandler<ValidationExceptionHandler>();
        services.AddExceptionHandler<DbUpdateExceptionHandler>();

        return services;
    }

    /// <summary>
    /// Registers the catch-all <see cref="FallbackExceptionHandler"/> LAST in the walk order.
    /// MUST be called after <c>AddBaseApi</c> and after <c>AddAppFeatures</c> so domain handlers
    /// (e.g. <c>OrchestrationValidationExceptionHandler</c>) register ahead of it (Phase 14 D-04).
    /// Walk order == registration order; first handler to return <c>true</c> claims.
    /// </summary>
    public static IServiceCollection AddBaseApiFallbackHandler(this IServiceCollection services)
    {
        services.AddExceptionHandler<FallbackExceptionHandler>();
        return services;
    }
}
