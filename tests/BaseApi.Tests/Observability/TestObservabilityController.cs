using BaseApi.Tests.Middleware;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Test-only endpoints used by Phase 5 verification battery. NOT registered in
/// <c>src/BaseApi.Service/</c> — discovered through the test assembly's
/// <c>AddApplicationPart</c> call (originally on the Phase 5 fixture; now on
/// <see cref="Phase11WebAppFactory"/> and <see cref="BaseApi.Tests.Composition.Phase8WebAppFactory"/>
/// after Plan 11-08c retired the prior fixture).
///
/// <para>
/// <b>Endpoint coverage map (Plan 05-02 SC#):</b>
/// <list type="bullet">
///   <item><c>GET /test-obs/ok</c>          → 200 + ILogger&lt;TestObservabilityController&gt;.LogInformation
///         ("test-obs ok ran") — SC#1, SC#2 driver</item>
///   <item><c>GET /test-obs/db-roundtrip</c> → issues an Npgsql SELECT via TestErrorDbContext;
///         returns 200; produces a CHILD span — SC#5 + T-05-PII</item>
/// </list>
/// </para>
/// </summary>
[ApiController]
[Route("test-obs")]
public sealed class TestObservabilityController(ILogger<TestObservabilityController> log) : ControllerBase
{
    private readonly ILogger<TestObservabilityController> _log = log;

    [HttpGet("ok")]
    public IActionResult Ok2xx()
    {
        // Emit a sample Information log so LogExportTests can assert against it.
        // Phase 4 CorrelationIdMiddleware's BeginScope("CorrelationId", id) is the active
        // ambient scope — IncludeScopes=true exports it as a log attribute.
        _log.LogInformation("test-obs ok ran");
        return Ok(new { ok = true });
    }

    [HttpGet("db-roundtrip")]
    public async Task<IActionResult> DbRoundtrip(
        [FromServices] TestErrorDbContext db,
        [FromQuery] Guid id,
        CancellationToken ct)
    {
        // Issue a parametrized SELECT — the `id` parameter becomes $1 in the SQL template.
        // T-05-PII regression: db.statement span attribute should carry only "$1" placeholder,
        // not the bound Guid value.
        var row = await db.Parents.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return Ok(new { exists = row is not null });
    }
}
