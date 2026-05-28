using BaseApi.Core.Exceptions;
using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Repositories;
using BaseApi.Core.Services;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BaseApi.Service.Features.Processor;

/// <summary>
/// Marker service for <see cref="ProcessorEntity"/>. Processor has only scalar FK
/// references (no junction tables — M2M graph lives at Step/Assignment/Workflow levels),
/// so the passthrough ctor + empty body suffices for the 6-step <c>CreateAsync</c> verb
/// order inherited from <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/>.
/// <para>
/// Phase 9 REQ-1: adds <see cref="GetBySourceHashAsync"/> — a single-row lookup on the
/// processor-specific <c>SourceHash</c> column. Lives here (not on
/// <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/>) because <c>SourceHash</c>
/// is processor-specific (CONTEXT D-03). Uses <c>DbContext.Set&lt;ProcessorEntity&gt;()</c>
/// directly per Phase 3 D-04 (IRepository&lt;T&gt; stays at exactly 5 methods).
/// The injected <see cref="IEntityMapper{TEntity,TCreate,TUpdate,TRead}"/> is a second
/// reference to the same DI-registered Mapperly mapper that <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/>
/// also holds — the base's <c>_mapper</c> field is private so we cannot reach it; the
/// duplicate ctor injection is cheap (the singleton mapper is the same instance).
/// </para>
/// </summary>
public sealed class ProcessorService :
    BaseService<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto>
{
    private readonly IEntityMapper<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto> _mapper;

    public ProcessorService(
        IValidator<ProcessorCreateDto> createValidator,
        IValidator<ProcessorUpdateDto> updateValidator,
        IEntityMapper<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto> mapper,
        IRepository<ProcessorEntity> repo,
        BaseDbContext dbContext)
        : base(createValidator, updateValidator, mapper, repo, dbContext)
    {
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    /// <summary>
    /// GET /api/v1/processors/by-source-hash/{sourceHash} — Phase 9 REQ-1.
    /// Direct <c>DbContext.Set&lt;ProcessorEntity&gt;()</c> + <c>AsNoTracking()</c> + predicate-based
    /// <c>FirstOrDefaultAsync</c> on the <c>SourceHash</c> column (CONTEXT D-01 + D-02).
    /// Throws <see cref="NotFoundException"/> on miss — Phase 4 NotFoundExceptionHandler
    /// maps to 404 ProblemDetails with <c>resourceType=ProcessorEntity</c> and
    /// <c>resourceId=&lt;the sourceHash string&gt;</c>. Off-format hashes (non-hex, wrong
    /// length) simply 404 via row-miss — no route-level validation (SPEC.md Constraint).
    /// </summary>
    /// <remarks>
    /// IN-01 (iteration 2) — case sensitivity contract: callers MUST supply the
    /// <paramref name="sourceHash"/> lowercased. <see cref="ProcessorCreateDtoValidator"/>
    /// enforces a lowercase 64-char hex string at create time, and this lookup performs
    /// a case-sensitive equality match (<c>p.SourceHash == sourceHash</c>) — uppercase
    /// (or mixed-case) variants will 404 even when a row with the same logical hash
    /// exists. This is the intentional shape per SPEC.md Constraint ("no route-level
    /// validation") and CONTEXT D-03 ("off-format strings 404 via row-miss"); we do
    /// NOT normalize on the read path because doing so would silently accept inputs
    /// the validator rejects on writes. Option (a) from REVIEW IN-01
    /// (<c>sourceHash.ToLowerInvariant()</c>) was deliberately rejected to preserve
    /// strict round-tripping with the create-time format contract.
    /// </remarks>
    public async Task<ProcessorReadDto> GetBySourceHashAsync(string sourceHash, CancellationToken ct)
    {
        // WR-02 guard: a null/empty/whitespace sourceHash cannot match any row
        // (the validator enforces a 64-char lowercase hex string at create time).
        // Short-circuit to a 404 to avoid an unnecessary DB round-trip and a
        // misleading 404 with resourceId="". The 404 here mirrors the existing
        // "miss => 404" contract — public method reachable from non-controller
        // callers (CONTEXT D-05 pre-injected mappers anticipate cross-entity reuse).
        if (string.IsNullOrWhiteSpace(sourceHash))
            throw new NotFoundException(nameof(ProcessorEntity), sourceHash ?? "(null)");

        var entity = await DbContext.Set<ProcessorEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.SourceHash == sourceHash, ct);
        if (entity is null) throw new NotFoundException(nameof(ProcessorEntity), sourceHash);
        return _mapper.ToRead(entity);
    }
}
