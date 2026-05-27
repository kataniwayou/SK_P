using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Repositories;
using BaseApi.Core.Services;
using FluentValidation;

namespace BaseApi.Service.Features.Processor;

/// <summary>
/// Marker service for <see cref="ProcessorEntity"/>. Processor has only scalar FK
/// references (no junction tables — M2M graph lives at Step/Assignment/Workflow levels),
/// so the 5-param passthrough ctor and an empty body are sufficient — the 6-step
/// <c>CreateAsync</c> verb order is inherited verbatim from
/// <see cref="BaseService{TEntity,TCreate,TUpdate,TRead}"/>.
/// </summary>
public sealed class ProcessorService :
    BaseService<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto>
{
    public ProcessorService(
        IValidator<ProcessorCreateDto> createValidator,
        IValidator<ProcessorUpdateDto> updateValidator,
        IEntityMapper<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto> mapper,
        IRepository<ProcessorEntity> repo,
        BaseDbContext dbContext)
        : base(createValidator, updateValidator, mapper, repo, dbContext) { }
}
