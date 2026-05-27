using System.Reflection;
using BaseApi.Core.Mapping;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Auto-discovers all closed-generic
/// <see cref="IEntityMapper{TEntity,TCreate,TUpdate,TRead}"/> implementations
/// in the supplied assemblies and registers each as
/// <see cref="ServiceLifetime.Singleton"/> (Phase 6 D-15). Closes the DI half of HTTP-10.
///
/// <para>
/// Mappers are stateless — Mapperly source-gen emits pure functions, no fields,
/// no captured services in v1. Phase 8 may introduce scoped dependencies (deferred
/// per CONTEXT Deferred Ideas).
/// </para>
///
/// <para>
/// <b>Reflection-scan safety (RESEARCH Pitfall 7):</b> uses
/// <see cref="Assembly.GetExportedTypes()"/> (public types only) instead of
/// <see cref="Assembly.GetTypes()"/> to avoid
/// <see cref="ReflectionTypeLoadException"/> on partially-built assemblies during
/// Roslyn incremental builds in IDE scenarios.
/// </para>
///
/// <para>
/// <b>Phase 7 contract:</b> identical to <see cref="ValidationServiceCollectionExtensions"/> —
/// <c>AddBaseApi</c> will absorb this call unchanged.
/// </para>
/// </summary>
public static class MappingServiceCollectionExtensions
{
    public static IServiceCollection AddBaseApiMapping(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;

                var closedInterfaces = type.GetInterfaces()
                    .Where(i => i.IsGenericType &&
                                i.GetGenericTypeDefinition() == typeof(IEntityMapper<,,,>));

                foreach (var closedInterface in closedInterfaces)
                {
                    services.AddSingleton(closedInterface, type);
                }
            }
        }
        return services;
    }
}
