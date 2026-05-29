using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Orchestration;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// Real L3 → L1 build (L1-BUILD-02/03/04). Staged: (1) load the requested workflows and their
    /// junction edges (entry steps + assignments) via the <c>WorkflowEntrySteps</c> /
    /// <c>WorkflowAssignments</c> junctions; (2) walk the reachable step graph breadth-first over the
    /// <c>StepNextSteps</c> junction (cycle-terminating); (3) batch-load the dependent processors,
    /// schemas and assignments; (4) map every entity via its Mapperly <c>ToRead</c> seam (D-05) and
    /// enrich the junction-backed collections via <c>with { ... }</c> (D-06). All reads are
    /// <c>AsNoTracking</c> batch queries against <see cref="BaseDbContext"/> — never <c>Repository&lt;T&gt;</c>
    /// (L1-BUILD-02). The snapshot is constructed with the injected logger so its own <c>Dispose()</c>
    /// emits the D-04 "L1 snapshot disposed" line (the loader does not log disposal itself).
    /// </summary>
    public async Task<WorkflowGraphSnapshot> LoadL1Async(IReadOnlyList<Guid> workflowIds, CancellationToken ct)
    {
        // STAGE 1 — workflows + their junction edges (entry steps + assignments).
        var workflows = await _db.Set<WorkflowEntity>().AsNoTracking()
            .Where(w => workflowIds.Contains(w.Id)).ToListAsync(ct);

        var entryRows = await _db.Set<WorkflowEntrySteps>().AsNoTracking()
            .Where(j => workflowIds.Contains(j.WorkflowId)).ToListAsync(ct);
        var entryLookup = entryRows.GroupBy(j => j.WorkflowId)
            .ToDictionary(g => g.Key, g => g.Select(j => j.StepId).ToList());

        var wfAssignmentRows = await _db.Set<WorkflowAssignments>().AsNoTracking()
            .Where(j => workflowIds.Contains(j.WorkflowId)).ToListAsync(ct);
        var assignmentLookup = wfAssignmentRows.GroupBy(j => j.WorkflowId)
            .ToDictionary(g => g.Key, g => g.Select(j => j.AssignmentId).ToList());

        // STAGE 2 — breadth-first step traversal over the StepNextSteps junction (cycle-terminating).
        var allEntryStepIds = entryLookup.Values.SelectMany(x => x).Distinct().ToList();
        var (stepEntities, nextStepLookup) = await LoadStepsBreadthFirstAsync(allEntryStepIds, ct);

        // STAGE 3 — batched dependents (all step ids now known).
        var processorIds = stepEntities.Select(s => s.ProcessorId).Distinct().ToList();
        var processors = await _db.Set<ProcessorEntity>().AsNoTracking()
            .Where(p => processorIds.Contains(p.Id)).ToListAsync(ct);

        var schemaIds = processors
            .SelectMany(p => new[] { p.InputSchemaId, p.OutputSchemaId, p.ConfigSchemaId })
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        var schemas = await _db.Set<SchemaEntity>().AsNoTracking()
            .Where(s => schemaIds.Contains(s.Id)).ToListAsync(ct);

        var assignmentIds = assignmentLookup.Values.SelectMany(x => x).Distinct().ToList();
        var assignments = await _db.Set<AssignmentEntity>().AsNoTracking()
            .Where(a => assignmentIds.Contains(a.Id)).ToListAsync(ct);

        // STAGE 4 — map via Mapperly ToRead (D-05) + enrich Step/Workflow junction collections (D-06).
        // Construct the snapshot with the injected logger (D-04 disposal log owned by the record).
        var snapshot = new WorkflowGraphSnapshot(_logger);

        foreach (var s in schemas)     snapshot.Schemas[s.Id]     = _schemaMapper.ToRead(s);
        foreach (var p in processors)  snapshot.Processors[p.Id]  = _processorMapper.ToRead(p);
        foreach (var a in assignments) snapshot.Assignments[a.Id] = _assignmentMapper.ToRead(a);

        foreach (var st in stepEntities)
        {
            var dto = _stepMapper.ToRead(st);                                  // NextStepIds == null
            var children = nextStepLookup.GetValueOrDefault(st.Id) ?? new List<Guid>();
            snapshot.Steps[st.Id] = dto with { NextStepIds = children };       // enrich
        }

        foreach (var wf in workflows)
        {
            var dto = _workflowMapper.ToRead(wf);                              // EntryStepIds/AssignmentIds null
            var entry = entryLookup.GetValueOrDefault(wf.Id) ?? new List<Guid>();
            var asg   = assignmentLookup.GetValueOrDefault(wf.Id) ?? new List<Guid>();
            snapshot.Workflows[wf.Id] = dto with { EntryStepIds = entry, AssignmentIds = asg };  // enrich
        }

        return snapshot;
    }

    // Plan 13-02 Task 2 replaces this stub with the iterative depth-wave BFS over StepNextSteps.
    private Task<(List<StepEntity> Steps, Dictionary<Guid, List<Guid>> NextStepLookup)>
        LoadStepsBreadthFirstAsync(IReadOnlyList<Guid> entryStepIds, CancellationToken ct)
        => Task.FromResult((new List<StepEntity>(), new Dictionary<Guid, List<Guid>>()));
}
