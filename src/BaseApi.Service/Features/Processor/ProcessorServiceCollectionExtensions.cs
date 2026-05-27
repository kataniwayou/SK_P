using BaseApi.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Service.Features.Processor;

/// <summary>
/// Per-entity DI extension for the Processor feature. Wave C 08-07 composes the 5 per-entity
/// extensions (<c>AddSchemaFeature</c>, <c>AddProcessorFeature</c>, <c>AddStepFeature</c>,
/// <c>AddAssignmentFeature</c>, <c>AddWorkflowFeature</c>) inside a single
/// <c>AddAppFeatures()</c> aggregator, invoked from <c>Program.cs</c> after
/// <c>services.AddBaseApi&lt;AppDbContext&gt;(...)</c>.
/// <para>
/// The abstract-base <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/> alias is
/// LOAD-BEARING because <see cref="ProcessorsController"/>.ctor injects the abstract type
/// (Phase 7 Warning 7 option b) — without the alias, DI cannot resolve the controller's
/// dependency.
/// </para>
/// </summary>
internal static class ProcessorServiceCollectionExtensions
{
    public static IServiceCollection AddProcessorFeature(this IServiceCollection services)
    {
        services.AddScoped<ProcessorService>();
        services.AddScoped<BaseService<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto>>(
            sp => sp.GetRequiredService<ProcessorService>());
        return services;
    }
}
