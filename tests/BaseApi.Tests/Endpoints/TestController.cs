using System.ComponentModel.DataAnnotations;
using BaseApi.Core.Exceptions;
using BaseApi.Tests.Middleware;
using BaseApi.Tests.Validation;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BaseApi.Tests.Endpoints;

/// <summary>
/// Deliberately-throwing endpoints used ONLY by Phase 4 verification
/// (<see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> integration tests).
/// NOT FOR PRODUCTION USE — parameters are unbounded and inputs are unvalidated.
///
/// <para>Test-only controller hosting one endpoint per handler path. Discovered by
/// <see cref="BaseApi.Tests.Middleware.WebAppFactory"/> via
/// <c>AddApplicationPart(typeof(WebAppFactory).Assembly)</c> — does NOT exist in
/// <c>src/BaseApi.Service/</c>, which has zero controllers in Phase 4.</para>
///
/// <para>
/// <b>Endpoint coverage map:</b>
/// <list type="bullet">
///   <item><c>GET    /test/ok</c>                              → 200 (CorrelationId echo on 2xx — SC#1)</item>
///   <item><c>GET    /test/not-found</c>                        → throws <see cref="NotFoundException"/> → 404 (SC#4 / ERROR-06)</item>
///   <item><c>GET    /test/unhandled</c>                        → throws <see cref="InvalidOperationException"/> → 500 (SC#4 / ERROR-07 / T-04-LEAK)</item>
///   <item><c>POST   /test/validation-error-via-fv</c>          → throws <see cref="ValidationException"/> → 400 (SC#2 / ERROR-03)</item>
///   <item><c>POST   /test/validation-error-via-modelbinding</c> → <c>[Required]</c> DTO; missing field → 400 model-binding (SC#5 / ERROR-10)</item>
///   <item><c>POST   /test/fk-violation</c>                     → EF SaveChanges with non-existent ParentId → 23503 → 422 (SC#3 / ERROR-04)</item>
///   <item><c>POST   /test/unique-violation</c>                 → EF SaveChanges with duplicate Name → 23505 → 409 (SC#3 / ERROR-05)</item>
///   <item><c>POST   /test/concurrency</c>                      → two SaveChanges racing on same row → DbUpdateConcurrencyException → 409 (D-03a / T-04-XMIN)</item>
///   <item><c>POST   /test/validate</c>                        → 400 ProblemDetails (SC#3 / VALID-03 — Service-layer FluentValidation explicit ValidateAsync)</item>
/// </list>
/// </para>
/// </summary>
[ApiController]
[Route("test")]
public sealed class TestController : ControllerBase
{
    [HttpGet("ok")]
    public IActionResult Ok2xx() => Ok(new { ok = true });

    [HttpGet("not-found")]
    public IActionResult NotFoundThrows() =>
        throw new NotFoundException("Schema", Guid.NewGuid());

    [HttpGet("unhandled")]
    public IActionResult Unhandled() =>
        throw new InvalidOperationException("This message should NOT leak to the client body");

    public sealed class ValidationDto
    {
        [Required] public string? Name { get; set; }
    }

    [HttpPost("validation-error-via-fv")]
    public IActionResult FluentValidationThrows()
    {
        var failures = new[]
        {
            new ValidationFailure("Version", "Version must be SemVer."),
            new ValidationFailure("Name", "Name is required."),
        };
        throw new FluentValidation.ValidationException(failures);
    }

    [HttpPost("validation-error-via-modelbinding")]
    public IActionResult ModelBindingTrigger([FromBody] ValidationDto dto) =>
        Ok(dto);  // never reached when Name missing — [ApiController] short-circuits with 400

    [HttpPost("fk-violation")]
    public async Task<IActionResult> FkViolation(
        [FromServices] TestErrorDbContext db,
        CancellationToken ct)
    {
        var child = new TestChildEntity
        {
            Name = $"child-{Guid.NewGuid():N}",
            Version = "1.0.0",
            ParentId = Guid.NewGuid(),  // non-existent → triggers 23503
        };
        db.Children.Add(child);
        await db.SaveChangesAsync(ct);  // throws DbUpdateException with PostgresException inner SqlState=23503
        return Ok();
    }

    [HttpPost("unique-violation")]
    public async Task<IActionResult> UniqueViolation(
        [FromServices] TestErrorDbContext db,
        [FromQuery] string name,
        [FromQuery] Guid parentId,
        CancellationToken ct)
    {
        // Caller seeded a parent with `parentId` + a first child with `name` out-of-band
        // (via separate TestErrorDbContext); this insert duplicates the unique Name → 23505.
        var dup = new TestChildEntity
        {
            Name = name,
            Version = "1.0.0",
            ParentId = parentId,
        };
        db.Children.Add(dup);
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("concurrency")]
    public async Task<IActionResult> Concurrency(
        [FromServices] TestErrorDbContext db,
        [FromQuery] Guid id,
        [FromQuery] int delayMs,
        CancellationToken ct)
    {
        var entity = await db.Parents.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity is null) return NotFound();
        // Optional load→save barrier (delayMs > 0): hold AFTER the read so two concurrent callers
        // both load the SAME xmin before either commits, making the DbUpdateConcurrencyException
        // deterministic. Without it the two POSTs can serialize by chance (request 1 commits before
        // request 2 reads → request 2 sees the advanced xmin → no conflict → flaky 409). 0 = no delay
        // (default), preserving the original behavior for any other caller.
        if (delayMs > 0) await Task.Delay(delayMs, ct);
        // Mutate; when this SaveChangesAsync runs against a row whose xmin has advanced under it
        // (the racing committer won), EF raises DbUpdateConcurrencyException → 409.
        entity.Name = $"updated-{Guid.NewGuid():N}";
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("validate")]
    public async Task<IActionResult> Validate(
        [FromServices] TestValidationService svc,
        [FromBody] TestUpdateDto dto,
        CancellationToken ct)
    {
        await svc.ValidateAsync(dto, ct);  // throws FluentValidation.ValidationException on invalid
        return Ok();
    }
}
