using Asp.Versioning;
using BaseApi.Core.Contracts;
using BaseApi.Core.Entities;
using BaseApi.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BaseApi.Core.Controllers;

/// <summary>
/// Abstract generic base controller exposing 5 CRUD verbs against
/// <typeparamref name="TEntity"/>. Concrete controllers inherit with no body — the verbs
/// come from this base, the URL prefix <c>/api/v1/{entity}</c> comes from the
/// <c>[Route]</c> attribute below (URL-segment versioning per HTTP-15 / CONTEXT D-17).
/// </summary>
/// <typeparam name="TEntity">Concrete <see cref="BaseEntity"/> subclass — Phase 8 entities.</typeparam>
/// <typeparam name="TCreate">DTO used in POST body (excludes server-controlled fields per HTTP-05).</typeparam>
/// <typeparam name="TUpdate">DTO used in PUT body (excludes Id + CreatedAt + CreatedBy per HTTP-06).</typeparam>
/// <typeparam name="TRead">DTO returned to clients (implements <see cref="IHasId"/> per HTTP-07).</typeparam>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public abstract class BaseController<TEntity, TCreate, TUpdate, TRead> : ControllerBase
    where TEntity : BaseEntity
    where TRead   : IHasId
{
    private readonly BaseService<TEntity, TCreate, TUpdate, TRead> _service;

    /// <summary>Concrete controllers pass through the matching concrete service.</summary>
    protected BaseController(BaseService<TEntity, TCreate, TUpdate, TRead> service)
        => _service = service;

    /// <summary>GET /api/v1/{entity} — returns the full list as a bare JSON array (CONTEXT D-04).</summary>
    /// <remarks>
    /// DEVIATION (Rule 1 — C# language constraint): the original plan body specified
    /// <c>[ProducesResponseType(typeof(IReadOnlyList&lt;TRead&gt;), ...)]</c> but C# attribute
    /// arguments cannot use generic type parameters (CS0416). Only status-code-only and
    /// non-generic-type variants are emitted here; ActionResult&lt;TRead&gt; still surfaces the
    /// generic schema on the Swagger document via the action return type. Concrete Phase 8
    /// controllers MAY add typed <c>[ProducesResponseType(typeof(ConcreteReadDto), 200)]</c>
    /// in their bodies if they want explicit per-status schemas.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TRead>>> List(CancellationToken ct)
        => Ok(await _service.ListAsync(ct));

    /// <summary>GET /api/v1/{entity}/{id} — 404 if missing (Phase 4 NotFoundExceptionHandler).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TRead>> GetById(Guid id, CancellationToken ct)
        => Ok(await _service.GetByIdAsync(id, ct));

    /// <summary>
    /// POST /api/v1/{entity} — 201 + Location header /api/v1/{entity}/{newId} + body=TRead (CONTEXT D-01).
    /// Phase 4 ValidationExceptionHandler maps any FluentValidation.ValidationException to 400.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TRead>> Create([FromBody] TCreate dto, CancellationToken ct)
    {
        var read = await _service.CreateAsync(dto, ct);
        return CreatedAtAction(nameof(GetById), new { id = read.Id }, read);
    }

    /// <summary>PUT /api/v1/{entity}/{id} — 200 + TRead body (CONTEXT D-02).</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TRead>> Update(Guid id, [FromBody] TUpdate dto, CancellationToken ct)
        => Ok(await _service.UpdateAsync(id, dto, ct));

    /// <summary>DELETE /api/v1/{entity}/{id} — 204 No Content; 404 if missing (CONTEXT D-03).</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return NoContent();
    }
}
