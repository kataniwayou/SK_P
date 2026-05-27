using BaseApi.Core.Entities;
using BaseApi.Core.Exceptions;
using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BaseApi.Core.Services;

/// <summary>
/// Abstract generic orchestrator. Owns the locked 6-step <see cref="CreateAsync"/> verb order:
/// <list type="number">
///   <item>validator.ValidateAndThrowAsync (Phase 6 / VALID-03 — explicit, not MVC auto-validation)</item>
///   <item>mapper.ToEntity (Phase 6 — Mapperly source-gen)</item>
///   <item>repo.AddAsync (Phase 3 — ChangeTracker state becomes Added)</item>
///   <item><see cref="SyncJunctionsAsync"/> (virtual hook — Phase 8 StepService + WorkflowService override)</item>
///   <item>dbContext.SaveChangesAsync (Phase 3 — AuditInterceptor stamps CreatedAt/UpdatedAt/CreatedBy/UpdatedBy; xmin advances)</item>
///   <item>mapper.ToRead (returns TRead with audit fields visible to caller)</item>
/// </list>
/// The same hook ordering is mirrored in <see cref="UpdateAsync"/> (minus step 3).
/// NotFound surfaces via <see cref="NotFoundException"/> — Phase 4 NotFoundExceptionHandler maps to 404.
/// DbUpdateConcurrencyException / DbUpdateException bubble to Phase 4 DbUpdateExceptionHandler (CONTEXT D-08).
/// </summary>
public abstract class BaseService<TEntity, TCreate, TUpdate, TRead>
    where TEntity : BaseEntity
{
    private readonly IValidator<TCreate> _createValidator;
    private readonly IValidator<TUpdate> _updateValidator;
    private readonly IEntityMapper<TEntity, TCreate, TUpdate, TRead> _mapper;
    private readonly IRepository<TEntity> _repo;
    private readonly ILogger _logger;

    /// <summary>
    /// Exposed as a property (not field) so derived services in Phase 8 can read the
    /// ChangeTracker inside their <see cref="SyncJunctionsAsync"/> override to enqueue
    /// junction-table entities under the same transaction (RESEARCH Pitfall 3).
    /// </summary>
    protected BaseDbContext DbContext { get; }

    /// <summary>
    /// Concrete services (Phase 7 RecordingTestService; Phase 8 SchemaService/ProcessorService/...)
    /// pass the 6 injectees to base. ASP.NET Core DI resolves all 6 from the AddBaseApi chain
    /// (validators from Phase 6 AddBaseApiValidation, mapper from Phase 6 AddBaseApiMapping,
    /// repo from Phase 7 AddBaseApiPersistence, DbContext from Phase 7 AddBaseApiPersistence,
    /// logger from the framework).
    /// </summary>
    protected BaseService(
        IValidator<TCreate> createValidator,
        IValidator<TUpdate> updateValidator,
        IEntityMapper<TEntity, TCreate, TUpdate, TRead> mapper,
        IRepository<TEntity> repo,
        BaseDbContext dbContext,
        ILogger<BaseService<TEntity, TCreate, TUpdate, TRead>> logger)
    {
        _createValidator = createValidator
            ?? throw new InvalidOperationException(
                $"No IValidator<{typeof(TCreate).Name}> registered. Concrete validator must " +
                $"inherit AbstractValidator<{typeof(TCreate).Name}> and be discoverable by " +
                "AddBaseApiValidation's AddValidatorsFromAssembly scan (Phase 6 / VALID-02).");
        _updateValidator = updateValidator
            ?? throw new InvalidOperationException(
                $"No IValidator<{typeof(TUpdate).Name}> registered. Concrete validator must " +
                $"inherit AbstractValidator<{typeof(TUpdate).Name}> and be discoverable by " +
                "AddBaseApiValidation's AddValidatorsFromAssembly scan (Phase 6 / VALID-02).");
        _mapper    = mapper    ?? throw new ArgumentNullException(nameof(mapper));
        _repo      = repo      ?? throw new ArgumentNullException(nameof(repo));
        DbContext  = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>GET /api/v1/{entity} — full list mapped to TRead.</summary>
    public async Task<IReadOnlyList<TRead>> ListAsync(CancellationToken ct)
    {
        var entities = await _repo.ListAsync(ct);
        return entities.Select(_mapper.ToRead).ToList();
    }

    /// <summary>GET /api/v1/{entity}/{id} — throws <see cref="NotFoundException"/> if missing.</summary>
    public async Task<TRead> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var entity = await _repo.GetAsync(id, ct);
        if (entity is null) throw new NotFoundException(typeof(TEntity).Name, id);
        return _mapper.ToRead(entity);
    }

    /// <summary>
    /// POST /api/v1/{entity} — locked 6-step order. Junction-table inserts (Phase 8 StepService /
    /// WorkflowService overrides of <see cref="SyncJunctionsAsync"/>) happen between Add and
    /// SaveChanges so the whole graph commits atomically; if any junction insert fails with
    /// SQLSTATE 23503 (FK violation), the parent entity also rolls back.
    /// </summary>
    public async Task<TRead> CreateAsync(TCreate dto, CancellationToken ct)
    {
        await _createValidator.ValidateAndThrowAsync(dto, ct);          // 1 — VALID-03 explicit
        var entity = _mapper.ToEntity(dto);                              // 2 — Mapperly
        await _repo.AddAsync(entity, ct);                                // 3 — tracker:Added
        await SyncJunctionsAsync(entity, dto, default, ct);              // 4 — virtual hook
        await DbContext.SaveChangesAsync(ct);                            // 5 — AuditInterceptor + xmin
        return _mapper.ToRead(entity);                                   // 6 — audit fields visible
    }

    /// <summary>PUT /api/v1/{entity}/{id} — mirrors CreateAsync minus step 3 (entity already exists).</summary>
    public async Task<TRead> UpdateAsync(Guid id, TUpdate dto, CancellationToken ct)
    {
        await _updateValidator.ValidateAndThrowAsync(dto, ct);
        var entity = await _repo.GetAsync(id, ct);
        if (entity is null) throw new NotFoundException(typeof(TEntity).Name, id);
        _mapper.Update(dto, entity);
        await SyncJunctionsAsync(entity, default, dto, ct);
        await DbContext.SaveChangesAsync(ct);
        return _mapper.ToRead(entity);
    }

    /// <summary>DELETE /api/v1/{entity}/{id} — load-then-remove (Phase 3 D-08).</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var existing = await _repo.GetAsync(id, ct);
        if (existing is null) throw new NotFoundException(typeof(TEntity).Name, id);
        await _repo.DeleteAsync(id, ct);
        await DbContext.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Phase 8 override site for M2M junction sync. Called AFTER <c>repo.AddAsync</c>
    /// (tracker: Added) and BEFORE <c>SaveChangesAsync</c>. Exactly one of
    /// <paramref name="createDto"/> or <paramref name="updateDto"/> is non-null.
    /// Default is no-op (CONTEXT D-10).
    /// </summary>
    protected virtual Task SyncJunctionsAsync(
        TEntity entity, TCreate? createDto, TUpdate? updateDto, CancellationToken ct)
        => Task.CompletedTask;
}
