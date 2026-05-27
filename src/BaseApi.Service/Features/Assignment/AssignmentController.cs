using BaseApi.Core.Controllers;
using BaseApi.Core.Services;

namespace BaseApi.Service.Features.Assignment;

/// <summary>
/// Concrete controller for the Assignment feature. Empty body — the 5 CRUD verbs are
/// inherited from <see cref="BaseController{TEntity,TCreate,TUpdate,TRead}"/>. The
/// URL prefix <c>/api/v1/assignments</c> comes from the <c>[controller]</c> token
/// convention (class-name "Assignments" minus "Controller" suffix).
/// <para>
/// Constructor injects the ABSTRACT
/// <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/> (NOT the concrete
/// <see cref="AssignmentService"/>) per Phase 7 Warning 7 option b — the DI alias
/// in <see cref="AssignmentServiceCollectionExtensions.AddAssignmentFeature"/> is
/// load-bearing.
/// </para>
/// </summary>
public sealed class AssignmentsController :
    BaseController<AssignmentEntity, AssignmentCreateDto, AssignmentUpdateDto, AssignmentReadDto>
{
    public AssignmentsController(
        BaseService<AssignmentEntity, AssignmentCreateDto, AssignmentUpdateDto, AssignmentReadDto> service)
        : base(service) { }
}
