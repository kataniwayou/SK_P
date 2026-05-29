using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BaseApi.Service.Features.Orchestration;

/// <summary>
/// Phase 9 cross-entity orchestration controller. Two endpoints — <c>Start</c> and
/// <c>Stop</c> — both accepting a bare <c>List&lt;Guid&gt;</c> JSON-array body of
/// Workflow ids. As of Phase 13 (D-07) Start and Stop call DISTINCT service methods
/// (<see cref="OrchestrationService.StartAsync"/> orchestrates the L1 build pipeline;
/// <see cref="OrchestrationService.StopAsync"/> is the extracted existence check) and
/// return <c>204 No Content</c> on success. No response body.
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
        await _service.StartAsync(workflowIds, ct);
        return NoContent();
    }

    /// <summary>
    /// POST /api/v1/orchestration/stop — Phase 9 REQ-4. As of Phase 13 (D-07) this
    /// calls the distinct <see cref="OrchestrationService.StopAsync"/> method (in
    /// v3.3.0 the same existence semantics as Start's step 1; Phase 15 swaps it to a
    /// Redis EXISTS check). Only the URL segment + service method differ from Start.
    /// </summary>
    [HttpPost("stop")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Stop([FromBody] List<Guid> workflowIds, CancellationToken ct)
    {
        await _service.StopAsync(workflowIds, ct);
        return NoContent();
    }
}
