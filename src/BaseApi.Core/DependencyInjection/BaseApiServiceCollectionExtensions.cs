using BaseApi.Core.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Composition root for BaseApi.Service. Chains 4 internal Phase 7 sub-extensions
/// (Persistence/Health/ErrorHandling/Http) + 2 already-public Phase 6 extensions
/// (Validation/Mapping) — 6 calls on IServiceCollection in the CONTEXT D-13 amended order
/// (Observability is invoked separately on IHostApplicationBuilder; see plan body
/// <c>&lt;context_deviation&gt;</c> block for the engineering rationale —
/// <c>builder.Logging.AddOpenTelemetry</c> requires <c>ILoggingBuilder</c>, which
/// IServiceCollection does not expose).
/// </summary>
public static class BaseApiServiceCollectionExtensions
{
    /// <summary>
    /// Public top-level entry. Constrained to <see cref="BaseDbContext"/> (stronger than
    /// CONTEXT D-13's <c>DbContext</c>) so the consumer's DbContext is guaranteed to carry
    /// the Phase 3 xmin shadow concurrency token and snake_case convention.
    /// </summary>
    /// <typeparam name="TDbContext">Phase 8's AppDbContext (or any BaseDbContext subclass).</typeparam>
    public static IServiceCollection AddBaseApi<TDbContext>(
        this IServiceCollection services, IConfiguration cfg)
        where TDbContext : BaseDbContext
        => services
            .AddBaseApiPersistence<TDbContext>(cfg)                              // 1 — Phase 3
            .AddBaseApiHealth(cfg)                                               // 2 — Phase 5 (needs cfg for AddNpgSql)
            .AddBaseApiErrorHandling()                                           // 3 — Phase 4
            .AddBaseApiHttp(cfg)                                                 // 4 — Phase 7 NEW
            .AddBaseApiValidation(typeof(TDbContext).Assembly)                   // 5 — Phase 6 (already public)
            .AddBaseApiMapping(typeof(TDbContext).Assembly)                      // 6 — Phase 6 (already public)
            .AddBaseApiRedis(cfg);                                               // 7 — Phase 12 NEW (INFRA-COMP-01; D-14)
    //  Observability is chained separately on the builder in Program.cs because it needs
    //  builder.Logging (ILoggingBuilder) — IServiceCollection alone cannot wire MEL.
}
