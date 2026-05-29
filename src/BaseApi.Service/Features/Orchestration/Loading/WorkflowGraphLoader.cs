using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Orchestration;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using Microsoft.Extensions.Logging;

namespace BaseApi.Service.Features.Orchestration.Loading;

/// <summary>
/// L1 loader seam (ORCH-SPLIT-02). In Plan 13-01 it returns an EMPTY snapshot so the
/// orchestrator shape is structurally final and independently buildable/testable
/// before the loader behavior lands (bisect-friendly, D-01). Plan 13-02 fills
/// <see cref="LoadL1Async"/> with batch <c>AsNoTracking</c> reads + BFS traversal +
/// Mapperly enrichment.
/// <para>
/// The 5 <see cref="IEntityMapper{TEntity,TCreate,TUpdate,TRead}"/> closed generics are
/// relocated here from <c>OrchestrationService</c> (D-05); they are read by Plan 13-02.
/// The logger is typed <see cref="ILogger{WorkflowGraphSnapshot}"/> (not
/// <c>ILogger&lt;WorkflowGraphLoader&gt;</c>) because it is passed straight into the
/// snapshot's ctor — the snapshot owns the D-04 disposal log line.
/// </para>
/// </summary>
internal sealed class WorkflowGraphLoader : IWorkflowGraphLoader
{
    private readonly BaseDbContext _db;
    private readonly ILogger<WorkflowGraphSnapshot> _logger;
    private readonly IEntityMapper<SchemaEntity,     SchemaCreateDto,     SchemaUpdateDto,     SchemaReadDto>     _schemaMapper;
    private readonly IEntityMapper<ProcessorEntity,  ProcessorCreateDto,  ProcessorUpdateDto,  ProcessorReadDto>  _processorMapper;
    private readonly IEntityMapper<StepEntity,       StepCreateDto,       StepUpdateDto,       StepReadDto>       _stepMapper;
    private readonly IEntityMapper<AssignmentEntity, AssignmentCreateDto, AssignmentUpdateDto, AssignmentReadDto> _assignmentMapper;
    private readonly IEntityMapper<WorkflowEntity,   WorkflowCreateDto,   WorkflowUpdateDto,   WorkflowReadDto>   _workflowMapper;

    public WorkflowGraphLoader(
        BaseDbContext db,
        ILogger<WorkflowGraphSnapshot> logger,
        IEntityMapper<SchemaEntity,     SchemaCreateDto,     SchemaUpdateDto,     SchemaReadDto>     schemaMapper,
        IEntityMapper<ProcessorEntity,  ProcessorCreateDto,  ProcessorUpdateDto,  ProcessorReadDto>  processorMapper,
        IEntityMapper<StepEntity,       StepCreateDto,       StepUpdateDto,       StepReadDto>       stepMapper,
        IEntityMapper<AssignmentEntity, AssignmentCreateDto, AssignmentUpdateDto, AssignmentReadDto> assignmentMapper,
        IEntityMapper<WorkflowEntity,   WorkflowCreateDto,   WorkflowUpdateDto,   WorkflowReadDto>   workflowMapper)
    {
        _db               = db               ?? throw new ArgumentNullException(nameof(db));
        _logger           = logger           ?? throw new ArgumentNullException(nameof(logger));
        _schemaMapper     = schemaMapper     ?? throw new ArgumentNullException(nameof(schemaMapper));
        _processorMapper  = processorMapper  ?? throw new ArgumentNullException(nameof(processorMapper));
        _stepMapper       = stepMapper       ?? throw new ArgumentNullException(nameof(stepMapper));
        _assignmentMapper = assignmentMapper ?? throw new ArgumentNullException(nameof(assignmentMapper));
        _workflowMapper   = workflowMapper   ?? throw new ArgumentNullException(nameof(workflowMapper));
    }

    // Plan 13-02 fills this with batch AsNoTracking reads + BFS traversal + Mapperly enrichment.
    // In Plan 13-01 it returns an empty snapshot (constructed with the logger so the snapshot can
    // emit the D-04 "L1 snapshot disposed" line on disposal) so the orchestrator shape is
    // structurally final and independently buildable/testable before the loader behavior lands
    // (bisect-friendly, D-01).
    public Task<WorkflowGraphSnapshot> LoadL1Async(IReadOnlyList<Guid> workflowIds, CancellationToken ct)
        => Task.FromResult(new WorkflowGraphSnapshot(_logger));
}
