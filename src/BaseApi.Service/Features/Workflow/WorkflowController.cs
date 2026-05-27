using BaseApi.Core.Controllers;
using BaseApi.Core.Services;

namespace BaseApi.Service.Features.Workflow;

/// <summary>
/// Concrete controller for the Workflow feature. Empty body — the 5 CRUD verbs are
/// inherited from <see cref="BaseController{TEntity,TCreate,TUpdate,TRead}"/>. The
/// URL prefix <c>/api/v1/workflows</c> comes from the <c>[controller]</c> token
/// convention (class-name "Workflows" minus "Controller" suffix).
/// <para>
/// Constructor injects the ABSTRACT
/// <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/> (NOT the concrete
/// <see cref="WorkflowService"/>) per Phase 7 Warning 7 option b — the DI alias
/// <c>AddScoped&lt;BaseService&lt;WorkflowEntity,...&gt;&gt;(sp =&gt; sp.GetRequiredService&lt;WorkflowService&gt;())</c>
/// in <see cref="WorkflowServiceCollectionExtensions.AddWorkflowFeature"/> is load-bearing.
/// </para>
/// </summary>
public sealed class WorkflowsController :
    BaseController<WorkflowEntity, WorkflowCreateDto, WorkflowUpdateDto, WorkflowReadDto>
{
    public WorkflowsController(
        BaseService<WorkflowEntity, WorkflowCreateDto, WorkflowUpdateDto, WorkflowReadDto> service)
        : base(service) { }
}
