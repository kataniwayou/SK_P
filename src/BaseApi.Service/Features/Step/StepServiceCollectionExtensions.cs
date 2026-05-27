using BaseApi.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Service.Features.Step;

/// <summary>
/// Per-entity DI extension for the Step feature. Wave C 08-07 composes the 5 per-entity
/// extensions (<c>AddSchemaFeature</c>, <c>AddProcessorFeature</c>, <c>AddStepFeature</c>,
/// <c>AddAssignmentFeature</c>, <c>AddWorkflowFeature</c>) inside a single
/// <c>AddAppFeatures()</c> aggregator, invoked from <c>Program.cs</c> after
/// <c>services.AddBaseApi&lt;AppDbContext&gt;(...)</c>.
/// <para>
/// The abstract-base <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/> alias is
/// LOAD-BEARING because <see cref="StepsController"/>.ctor injects the abstract type
/// (Phase 7 Warning 7 option b) — without the alias, DI cannot resolve the controller's
/// dependency.
/// </para>
/// </summary>
internal static class StepServiceCollectionExtensions
{
    public static IServiceCollection AddStepFeature(this IServiceCollection services)
    {
        services.AddScoped<StepService>();
        services.AddScoped<BaseService<StepEntity, StepCreateDto, StepUpdateDto, StepReadDto>>(
            sp => sp.GetRequiredService<StepService>());
        return services;
    }
}
