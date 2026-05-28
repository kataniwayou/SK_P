using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Orchestration;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Service.Composition;

/// <summary>
/// Wave C 08-07 aggregator that composes the 5 per-entity DI extensions
/// (<c>AddSchemaFeature</c>, <c>AddProcessorFeature</c>, <c>AddStepFeature</c>,
/// <c>AddAssignmentFeature</c>, <c>AddWorkflowFeature</c>) plus the Phase 9
/// Orchestration feature (<c>AddOrchestrationFeature</c>) into a single
/// <see cref="AddAppFeatures"/> call invoked from <c>Program.cs</c> after
/// <c>services.AddBaseApi&lt;AppDbContext&gt;(...)</c>. Each per-entity extension
/// registers a concrete <c>{Entity}Service</c> + the abstract-base
/// <c>BaseService&lt;...&gt;</c> alias that the matching empty-body controller
/// injects (Phase 7 Warning 7 option b). The Orchestration extension is simpler —
/// no abstract-base alias because <c>OrchestrationController</c> injects the
/// concrete <c>OrchestrationService</c> directly (Phase 9 CONTEXT D-06).
/// <para>
/// <c>internal</c> visibility — <c>Program.cs</c> lives in the same assembly so
/// no cross-assembly reach is needed; <c>BaseApi.Core</c> remains unaware of the
/// concrete entity types per the BaseApi.Core abstraction boundary.
/// </para>
/// </summary>
internal static class AppFeatures
{
    public static IServiceCollection AddAppFeatures(this IServiceCollection services)
    {
        services.AddSchemaFeature();
        services.AddProcessorFeature();
        services.AddStepFeature();
        services.AddAssignmentFeature();
        services.AddWorkflowFeature();
        services.AddOrchestrationFeature();   // Phase 9 REQ-2
        return services;
    }
}
