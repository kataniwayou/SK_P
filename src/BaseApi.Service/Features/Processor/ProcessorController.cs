using BaseApi.Core.Controllers;
using BaseApi.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BaseApi.Service.Features.Processor;

/// <summary>
/// Concrete controller for the Processor feature. Inherits the 5 CRUD verbs from
/// <see cref="BaseController{TEntity,TCreate,TUpdate,TRead}"/> (List / GetById /
/// Create / Update / Delete) — URL prefix <c>/api/v1/processors</c> comes from the
/// <c>[controller]</c> token convention.
/// <para>
/// Constructor injects BOTH the ABSTRACT
/// <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/> (Phase 7 Warning 7
/// option b — for the inherited verbs) AND the CONCRETE
/// <see cref="ProcessorService"/> (Phase 9 REQ-1 — for the new
/// <see cref="GetBySourceHash"/> action). The DI alias registered in
/// <see cref="ProcessorServiceCollectionExtensions.AddProcessorFeature"/> already
/// exposes both shapes (concrete + alias-to-abstract), so no DI change is required.
/// </para>
/// </summary>
public sealed class ProcessorsController :
    BaseController<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto>
{
    private readonly ProcessorService _processorService;

    public ProcessorsController(
        BaseService<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto> service,
        ProcessorService processorService)
        : base(service)
    {
        _processorService = processorService ?? throw new ArgumentNullException(nameof(processorService));
    }

    /// <summary>
    /// GET /api/v1/processors/by-source-hash/{sourceHash} — Phase 9 REQ-1.
    /// Returns the single processor whose <c>SourceHash</c> matches the literal
    /// route segment (case-sensitive, no validation — off-format hashes 404 via
    /// row-miss per CONTEXT D-03 / SPEC.md Constraint). 404 emitted by the Phase 4
    /// NotFoundExceptionHandler when <see cref="ProcessorService.GetBySourceHashAsync"/>
    /// throws <see cref="BaseApi.Core.Exceptions.NotFoundException"/>.
    /// </summary>
    [HttpGet("by-source-hash/{sourceHash}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProcessorReadDto>> GetBySourceHash(string sourceHash, CancellationToken ct)
        => Ok(await _processorService.GetBySourceHashAsync(sourceHash, ct));
}
