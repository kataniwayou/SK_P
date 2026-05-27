using BaseApi.Core.Configuration;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Interceptors;
using BaseApi.Core.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Phase 3 persistence wiring: DbContext + UseNpgsql + UseSnakeCaseNamingConvention +
/// AuditInterceptor (Singleton — RESEARCH Pitfall 4 reconciles CONTEXT D-14's Scoped
/// snippet against Phase 3 D-06 canonical) + IRepository&lt;&gt; open-generic +
/// BaseDbContext alias resolving to TDbContext (Scoped — RESEARCH Pitfall 5).
/// </summary>
internal static class PersistenceServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiPersistence<TDbContext>(
        this IServiceCollection services, IConfiguration cfg)
        where TDbContext : BaseDbContext
    {
        services.AddHttpContextAccessor();                                     // idempotent — Phase 4 also called
        services.AddSingleton(TimeProvider.System);                            // Phase 3 D-07
        services.AddSingleton<AuditInterceptor>();                             // Phase 3 D-06 canonical — Singleton

        services.AddDbContext<TDbContext>((sp, opts) =>
        {
            // WR-03: fail fast with a clear message rather than letting null propagate
            // into UseNpgsql which throws a less-clear error.
            opts.UseNpgsql(cfg.RequireConnectionString("Postgres"))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
        });

        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        // BaseDbContext alias — Scoped (matches AddDbContext default; PERSIST-15 locked) so
        // BaseService<...> can resolve the abstract type. Captive lifetime guarded against
        // by matching alias lifetime to TDbContext lifetime (both Scoped).
        services.AddScoped<BaseDbContext>(sp => sp.GetRequiredService<TDbContext>());
        return services;
    }
}
