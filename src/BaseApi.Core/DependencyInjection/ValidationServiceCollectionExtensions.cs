using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Wires FluentValidation 12.x validator auto-discovery via assembly scan.
/// Default lifetime: <see cref="ServiceLifetime.Scoped"/> — matches the
/// request-scoped pattern locked by PERSIST-15 (Phase 3). Closes VALID-01 + VALID-02.
///
/// <para>
/// <c>params Assembly[]</c> signature (Phase 6 D-16) supports both production wiring
/// (<c>AddBaseApiValidation(typeof(Program).Assembly)</c>) and test wiring
/// (<c>AddBaseApiValidation(typeof(Program).Assembly, typeof(WebAppFactory).Assembly)</c>).
/// </para>
///
/// <para>
/// <b>Phase 7 contract:</b> <c>AddBaseApi</c> composition root will absorb this call
/// with zero behavior change — Phase 6 places it at the precise location Phase 7
/// will compose from (between <c>AddProblemDetails</c> and the
/// <c>AddExceptionHandler</c> chain).
/// </para>
/// </summary>
public static class ValidationServiceCollectionExtensions
{
    public static IServiceCollection AddBaseApiValidation(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            services.AddValidatorsFromAssembly(
                assembly,
                lifetime: ServiceLifetime.Scoped,
                includeInternalTypes: false);
        }
        return services;
    }
}
