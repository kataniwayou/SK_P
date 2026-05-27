using BaseApi.Core.Controllers;
using BaseApi.Core.Services;

namespace BaseApi.Service.Features.Processor;

/// <summary>
/// Concrete controller for the Processor feature. Empty body — the 5 CRUD verbs are
/// inherited from <see cref="BaseController{TEntity,TCreate,TUpdate,TRead}"/>. The
/// URL prefix <c>/api/v1/processors</c> comes from the <c>[controller]</c> token
/// convention (class-name "Processors" minus "Controller" suffix).
/// <para>
/// Constructor injects the ABSTRACT
/// <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/> (NOT the concrete
/// <see cref="ProcessorService"/>) per Phase 7 Warning 7 option b — the DI alias
/// <c>AddScoped&lt;BaseService&lt;ProcessorEntity,...&gt;&gt;(sp =&gt; sp.GetRequiredService&lt;ProcessorService&gt;())</c>
/// in <see cref="ProcessorServiceCollectionExtensions.AddProcessorFeature"/> is load-bearing.
/// </para>
/// </summary>
public sealed class ProcessorsController :
    BaseController<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto>
{
    public ProcessorsController(
        BaseService<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto> service)
        : base(service) { }
}
