using Microsoft.EntityFrameworkCore;
using BaseApi.Core.Entities;
using BaseApi.Core.Persistence;

namespace BaseApi.Core.Persistence.Repositories;

/// <summary>
/// Concrete generic repository — sealed; load-then-remove DeleteAsync preserves the
/// D-03 xmin concurrency check. Constructor takes <see cref="BaseDbContext"/> (the abstract
/// base) so the type system enforces that only BaseApi.Core ecosystem DbContexts (with
/// snake_case + audit + xmin wired) can construct a Repository.
/// </summary>
public sealed class Repository<TEntity> : IRepository<TEntity> where TEntity : BaseEntity
{
    private readonly DbSet<TEntity> _set;

    public Repository(BaseDbContext db) => _set = db.Set<TEntity>();

    public Task<TEntity?> GetAsync(Guid id, CancellationToken cancellationToken)
        => _set.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TEntity>> ListAsync(CancellationToken cancellationToken)
        => await _set.ToListAsync(cancellationToken);

    public async Task AddAsync(TEntity entity, CancellationToken cancellationToken)
        => await _set.AddAsync(entity, cancellationToken);

    public void Update(TEntity entity) => _set.Update(entity);

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _set.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entity is null) return;
        _set.Remove(entity);
    }
}
