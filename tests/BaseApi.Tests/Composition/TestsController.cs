using BaseApi.Core.Controllers;
using BaseApi.Core.Services;
using BaseApi.Tests.Validation;          // TestEntity + 3 DTOs (Phase 6 scaffolds)

namespace BaseApi.Tests.Composition;

/// <summary>
/// Concrete derived controller proving SC#1 — empty body inherits all 5 verbs from
/// <see cref="BaseController{TEntity,TCreate,TUpdate,TRead}"/>. The class name plural
/// ("Tests") + ASP.NET Core's <c>[controller]</c> token convention strips the "Controller"
/// suffix to produce URL <c>/api/v1/tests</c>.
///
/// Constructor injects the ABSTRACT <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/>
/// (NOT the concrete <see cref="RecordingTestService"/>) per Warning 7 option b — Phase
/// 8 controllers will follow the same pattern so they remain concrete-service-name-free
/// reusable. Phase7WebAppFactory registers the alias
/// <c>AddScoped&lt;BaseService&lt;...&gt;&gt;(sp =&gt; sp.GetRequiredService&lt;RecordingTestService&gt;())</c>
/// which is now LOAD-BEARING (not dead code).
/// </summary>
public sealed class TestsController
    : BaseController<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>
{
    public TestsController(BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto> service)
        : base(service) { }
}
