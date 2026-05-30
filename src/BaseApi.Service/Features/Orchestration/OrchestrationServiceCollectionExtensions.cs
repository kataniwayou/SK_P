using BaseApi.Core.Configuration;
using BaseApi.Core.Persistence;
using BaseApi.Service.Features.Orchestration.Loading;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Orchestration.Validation;
using FluentValidation;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BaseApi.Service.Features.Orchestration;

/// <summary>
/// Per-feature DI extension for the Orchestration feature folder. Wired by
/// <see cref="BaseApi.Service.Composition.AppFeatures.AddAppFeatures"/> as the 6th
/// call after the 5 entity feature registrations.
/// <para>
/// <b>Simpler than the 5 entity extensions:</b> no abstract-base
/// <c>BaseService&lt;...&gt;</c> alias is needed because
/// <see cref="OrchestrationController"/> injects the concrete
/// <see cref="OrchestrationService"/> directly (CONTEXT D-06 — deliberate deviation
/// from Phase 7 Warning 7's abstract-base-injection pattern; there is no abstract
/// base for orchestration to inherit from).
/// </para>
/// <para>
/// <b>What this extension does NOT register (auto-discovered elsewhere):</b>
/// <list type="bullet">
///   <item><see cref="WorkflowIdsValidator"/> — auto-discovered by
///     <c>AddBaseApiValidation</c>'s <c>AddValidatorsFromAssembly</c> scan
///     (Phase 6 VALID-02).</item>
///   <item>All 5 entity mappers — auto-discovered by <c>AddBaseApiMapping</c>'s
///     closed-generic <c>IEntityMapper&lt;,,,&gt;</c> scan (Phase 6).</item>
///   <item><see cref="BaseApi.Core.Persistence.BaseDbContext"/> alias — registered
///     by <c>AddBaseApiPersistence</c> as Scoped (Phase 7 D-14).</item>
/// </list>
/// </para>
/// </summary>
internal static class OrchestrationServiceCollectionExtensions
{
    public static IServiceCollection AddOrchestrationFeature(this IServiceCollection services)
    {
        // Scoped — the loader + OrchestrationService both depend on the Scoped
        // BaseDbContext; Singleton would be a captive-dependency bug. The snapshot is
        // NOT registered (the loader constructs it via `new WorkflowGraphSnapshot(_logger)`);
        // only ILogger<WorkflowGraphSnapshot> is resolved by the standard logging provider.
        //
        // OrchestrationService is registered via an explicit factory (not the
        // typed-implementation overload) because its constructor is INTERNAL — its
        // signature exposes the internal seam types (IWorkflowGraphLoader,
        // CycleDetector, ...), which CS0051 forbids on a public constructor while the
        // class itself stays `public sealed` (Phase 9 D-06 — the controller injects
        // the concrete type). The default container's typed registration reflects for
        // a *public* constructor and would fail at ValidateOnBuild; the factory invokes
        // the internal ctor directly within this assembly.
        services.AddScoped<OrchestrationService>(sp => new OrchestrationService(
            sp.GetRequiredService<BaseDbContext>(),
            sp.GetRequiredService<IValidator<IReadOnlyList<Guid>>>(),
            sp.GetRequiredService<IWorkflowGraphLoader>(),
            sp.GetRequiredService<CycleDetector>(),
            sp.GetRequiredService<SchemaEdgeValidator>(),
            sp.GetRequiredService<PayloadConfigSchemaValidator>(),
            sp.GetRequiredService<IRedisProjectionWriter>(),
            sp.GetRequiredService<IRedisL2Cleanup>(),               // NEW (Plan 04) — Start pre-clean + Stop cleanup
            sp.GetRequiredService<IHttpContextAccessor>(),          // NEW (D-01) — correlationId resolution
            sp.GetRequiredService<IConnectionMultiplexer>(),        // NEW — Stop EXISTS gate
            sp.GetRequiredService<IPublishEndpoint>(),              // NEW (Plan 19-03) — publish Start/Stop (registered by AddBaseApiMessaging)
            sp.GetRequiredService<IOptions<RedisProjectionOptions>>())); // NEW — KeyPrefix for the Stop gate keys
        services.AddScoped<IWorkflowGraphLoader, WorkflowGraphLoader>();
        services.AddScoped<CycleDetector>();
        services.AddScoped<SchemaEdgeValidator>();
        services.AddScoped<PayloadConfigSchemaValidator>();
        services.AddScoped<IRedisProjectionWriter, RedisProjectionWriter>();
        // Shared, always-tolerant L2 cleanup routine (D-06 / D-07). Registered here in Plan 03
        // (not Plan 04) because Plan 03's StopCleanupFacts resolve IRedisL2Cleanup from DI; Plan 04
        // wires the two CALLERS (StopAsync gated, Start pre-clean tolerant). Scoped to match the
        // other Scoped orchestration seams (the multiplexer it depends on is Singleton — no captive bug).
        services.AddScoped<IRedisL2Cleanup, RedisL2Cleanup>();

        // D-04 ordering: registered here so it lands after the Core NotFound/Validation/DbUpdate
        // handlers and BEFORE the split-out FallbackExceptionHandler (AddBaseApiFallbackHandler is
        // called last in Program.cs, after AddAppFeatures runs this method). Reachable → emits 422.
        services.AddExceptionHandler<OrchestrationValidationExceptionHandler>();
        return services;
    }
}
