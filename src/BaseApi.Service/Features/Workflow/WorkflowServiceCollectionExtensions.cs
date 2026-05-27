using BaseApi.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Service.Features.Workflow;

/// <summary>
/// Per-entity DI extension for the Workflow feature. Wave C 08-07 composes the 5
/// per-entity extensions (<c>AddSchemaFeature</c>, <c>AddProcessorFeature</c>,
/// <c>AddStepFeature</c>, <c>AddAssignmentFeature</c>, <c>AddWorkflowFeature</c>)
/// inside a single <c>AddAppFeatures()</c> aggregator, invoked from
/// <c>Program.cs</c> after <c>services.AddBaseApi&lt;AppDbContext&gt;(...)</c>.
/// <para>
/// The abstract-base <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/> alias
/// is LOAD-BEARING because <see cref="WorkflowsController"/>.ctor injects the
/// abstract type (Phase 7 Warning 7 option b) — without the alias, DI cannot
/// resolve the controller's dependency.
/// </para>
/// </summary>
internal static class WorkflowServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowFeature(this IServiceCollection services)
    {
        services.AddScoped<WorkflowService>();
        services.AddScoped<BaseService<WorkflowEntity, WorkflowCreateDto, WorkflowUpdateDto, WorkflowReadDto>>(
            sp => sp.GetRequiredService<WorkflowService>());
        return services;
    }
}
