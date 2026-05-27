using BaseApi.Core.Controllers;
using BaseApi.Core.Services;

namespace BaseApi.Service.Features.Step;

/// <summary>
/// Concrete controller for the Step feature. Empty body — the 5 CRUD verbs are
/// inherited from <see cref="BaseController{TEntity,TCreate,TUpdate,TRead}"/>. The
/// URL prefix <c>/api/v1/steps</c> comes from the <c>[controller]</c> token
/// convention (class-name "Steps" minus "Controller" suffix).
/// <para>
/// Constructor injects the ABSTRACT
/// <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/> (NOT the concrete
/// <see cref="StepService"/>) per Phase 7 Warning 7 option b — the DI alias
/// <c>AddScoped&lt;BaseService&lt;StepEntity,...&gt;&gt;(sp =&gt; sp.GetRequiredService&lt;StepService&gt;())</c>
/// in <see cref="StepServiceCollectionExtensions.AddStepFeature"/> is load-bearing.
/// </para>
/// </summary>
public sealed class StepsController :
    BaseController<StepEntity, StepCreateDto, StepUpdateDto, StepReadDto>
{
    public StepsController(
        BaseService<StepEntity, StepCreateDto, StepUpdateDto, StepReadDto> service)
        : base(service) { }
}
