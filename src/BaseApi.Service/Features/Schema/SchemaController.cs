using BaseApi.Core.Controllers;
using BaseApi.Core.Services;

namespace BaseApi.Service.Features.Schema;

/// <summary>
/// Concrete controller for the Schema feature. Empty body — the 5 CRUD verbs are
/// inherited from <see cref="BaseController{TEntity,TCreate,TUpdate,TRead}"/>. The
/// URL prefix <c>/api/v1/schemas</c> comes from the <c>[controller]</c> token
/// convention (class-name "Schemas" minus "Controller" suffix).
/// <para>
/// Constructor injects the ABSTRACT
/// <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/> (NOT the concrete
/// <see cref="SchemaService"/>) per Phase 7 Warning 7 option b — the DI alias
/// <c>AddScoped&lt;BaseService&lt;SchemaEntity,...&gt;&gt;(sp =&gt; sp.GetRequiredService&lt;SchemaService&gt;())</c>
/// in <see cref="SchemaServiceCollectionExtensions.AddSchemaFeature"/> is load-bearing.
/// </para>
/// </summary>
public sealed class SchemasController :
    BaseController<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto>
{
    public SchemasController(
        BaseService<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto> service)
        : base(service) { }
}
