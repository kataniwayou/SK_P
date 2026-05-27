using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Repositories;
using BaseApi.Core.Services;
using FluentValidation;

namespace BaseApi.Service.Features.Schema;

/// <summary>
/// Marker service for <see cref="SchemaEntity"/>. Schema has no junction tables, so
/// the 5-param passthrough ctor and an empty body are sufficient — the 6-step
/// <c>CreateAsync</c> verb order is inherited verbatim from
/// <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/>.
/// </summary>
public sealed class SchemaService :
    BaseService<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto>
{
    public SchemaService(
        IValidator<SchemaCreateDto> createValidator,
        IValidator<SchemaUpdateDto> updateValidator,
        IEntityMapper<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto> mapper,
        IRepository<SchemaEntity> repo,
        BaseDbContext dbContext)
        : base(createValidator, updateValidator, mapper, repo, dbContext) { }
}
