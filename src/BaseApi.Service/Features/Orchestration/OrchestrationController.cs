using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BaseApi.Service.Features.Orchestration;

/// <summary>
/// Phase 9 cross-entity orchestration controller. Two endpoints — <c>Start</c> and
/// <c>Stop</c> — both accepting a bare <c>List&lt;Guid&gt;</c> JSON-array body of
/// Workflow ids. v1 behavior is validation-only (CONTEXT D-11 / SPEC.md amended
/// Requirements 3 + 4): both endpoints delegate to the same
/// <see cref="OrchestrationService.ValidateWorkflowIdsAsync"/> method (CONTEXT D-12)
/// and return <c>204 No Content</c> on success. No response body, no orchestration
/// side-effects.
/// <para>
/// <b>Singular class name (CONTEXT D-13):</b> this is the ONLY controller in the
/// codebase with a singular class name. All 5 entity controllers are plural
/// (<c>SchemasController</c>, <c>ProcessorsController</c>, etc.). The
/// <c>[controller]</c> token resolves to <c>orchestration</c> for the singular noun.
/// </para>
/// <para>
/// <b>Concrete-on-concrete (CONTEXT D-06):</b> ctor injects the concrete
/// <see cref="OrchestrationService"/> directly — no <c>IOrchestrationService</c>
/// interface, no abstract-base alias. Phase 7 Warning 7 abstract-base-injection
/// pattern does NOT apply (there is no abstract base to inherit from).
/// </para>
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public sealed class OrchestrationController : ControllerBase
{
    private readonly OrchestrationService _service;

    public OrchestrationController(OrchestrationService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    /// <summary>
    /// POST /api/v1/orchestration/start — Phase 9 REQ-3. v1 validation-only:
    /// runs <see cref="WorkflowIdsValidator"/> + Workflow existence check and
    /// returns <c>204 No Content</c> on success. No body. No orchestration
    /// side-effects (SPEC.md amended Acceptance Criteria 2026-05-28).
    /// </summary>
    [HttpPost("start")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Start([FromBody] List<Guid> workflowIds, CancellationToken ct)
    {
        await _service.ValidateWorkflowIdsAsync(workflowIds, ct);
        return NoContent();
    }

    /// <summary>
    /// POST /api/v1/orchestration/stop — Phase 9 REQ-4. Behaviorally identical to
    /// <see cref="Start"/> in v1 (CONTEXT D-12 — delegates to the same service
    /// method). Only the URL segment differs. Future phases that introduce real
    /// Start-vs-Stop divergence will split into separate service methods.
    /// </summary>
    [HttpPost("stop")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Stop([FromBody] List<Guid> workflowIds, CancellationToken ct)
    {
        await _service.ValidateWorkflowIdsAsync(workflowIds, ct);
        return NoContent();
    }
}
