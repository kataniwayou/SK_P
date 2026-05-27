using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Repositories;
using BaseApi.Core.Services;
using FluentValidation;

namespace BaseApi.Service.Features.Assignment;

/// <summary>
/// Marker service for <see cref="AssignmentEntity"/>. Assignment is a leaf entity with
/// no junction tables, so the 5-param passthrough ctor and an empty body are
/// sufficient — the 6-step <c>CreateAsync</c> verb order is inherited verbatim from
/// <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/>.
/// </summary>
public sealed class AssignmentService :
    BaseService<AssignmentEntity, AssignmentCreateDto, AssignmentUpdateDto, AssignmentReadDto>
{
    public AssignmentService(
        IValidator<AssignmentCreateDto> createValidator,
        IValidator<AssignmentUpdateDto> updateValidator,
        IEntityMapper<AssignmentEntity, AssignmentCreateDto, AssignmentUpdateDto, AssignmentReadDto> mapper,
        IRepository<AssignmentEntity> repo,
        BaseDbContext dbContext)
        : base(createValidator, updateValidator, mapper, repo, dbContext) { }
}
