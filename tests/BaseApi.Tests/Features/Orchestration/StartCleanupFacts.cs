using BaseApi.Service.Features.Orchestration;
using BaseApi.Service.Features.Orchestration.Loading;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Composition;
using BaseApi.Tests.TestHelpers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 13 SC4 — THE acceptance gate for the L1 cleanup contract (L1-BUILD-05 /
/// D-04). Forces a throw mid-Start AFTER the loader has returned a fully-populated
/// snapshot (via a throwing <see cref="IRedisProjectionWriter"/>, the LAST seam call)
/// and a recording <see cref="IWorkflowGraphLoader"/> that captures the snapshot
/// instance. Asserts the captured snapshot was DISPOSED on the throw path:
/// <c>IsDisposed == true</c> AND all 5 dictionaries <c>Count == 0</c> — proving the
/// <c>using</c> declaration in <c>OrchestrationService.StartAsync</c> runs
/// <c>Dispose()</c> on the failure path.
/// <para>
/// Both test doubles are reachable via <c>InternalsVisibleTo("BaseApi.Tests")</c>
/// (Plan 13-01). They are registered ONLY for this fact via
/// <c>ConfigureTestServices</c> (T-13-09 — production DI is unaffected).
/// </para>
/// </summary>
[Trait("Phase", "13")]
public sealed class StartCleanupFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public StartCleanupFacts(Phase8WebAppFactory factory) => _factory = factory;

    /// <summary>
    /// Recording loader double — wraps the REAL <see cref="WorkflowGraphLoader"/>,
    /// invokes the real <c>LoadL1Async</c>, and stashes the returned snapshot so the
    /// test can inspect its disposal state AFTER the request completes.
    /// </summary>
    private sealed class RecordingWorkflowGraphLoader : IWorkflowGraphLoader
    {
        private readonly IWorkflowGraphLoader _inner;
        public WorkflowGraphSnapshot? Captured { get; private set; }

        public RecordingWorkflowGraphLoader(IWorkflowGraphLoader inner) => _inner = inner;

        public async Task<WorkflowGraphSnapshot> LoadL1Async(
            IReadOnlyList<Guid> workflowIds, CancellationToken ct)
        {
            Captured = await _inner.LoadL1Async(workflowIds, ct);
            return Captured;
        }
    }

    /// <summary>
    /// Throwing projection-writer double — fires LAST in the StartAsync chain (step 6),
    /// AFTER the loader returns + the no-op validators run, proving disposal survives a
    /// late-pipeline throw. <see cref="InvalidOperationException"/> is not a domain
    /// exception so the IExceptionHandler chain falls through to the generic 500.
    /// </summary>
    private sealed class ThrowingRedisProjectionWriter : IRedisProjectionWriter
    {
        public Task UpsertAsync(WorkflowGraphSnapshot snapshot, CancellationToken ct)
            => throw new InvalidOperationException("forced throw for SC4 cleanup gate");
    }

    /// <summary>Seeds a Workflow via the public HTTP API (Processor → Step → Workflow).</summary>
    private static async Task<Guid> SeedWorkflowAsync(HttpClient client, CancellationToken ct)
    {
        var procDto = new ProcessorCreateDto(
            Name: $"clf-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: HashHelpers.RandomSha256Hex(),
            InputSchemaId: null,
            OutputSchemaId: null,
            ConfigSchemaId: null);
        var procResp = await client.PostAsJsonAsync("/api/v1/processors", procDto, ct);
        procResp.EnsureSuccessStatusCode();
        var proc = await procResp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);

        var stepDto = new StepCreateDto(
            Name: $"clf-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: proc!.Id,
            NextStepIds: null,
            EntryCondition: StepEntryCondition.PreviousCompleted);
        var stepResp = await client.PostAsJsonAsync("/api/v1/steps", stepDto, ct);
        stepResp.EnsureSuccessStatusCode();
        var step = await stepResp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);

        var wfDto = new WorkflowCreateDto(
            Name: $"clf-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: new List<Guid> { step!.Id },
            AssignmentIds: null,
            CronExpression: null);
        var wfResp = await client.PostAsJsonAsync("/api/v1/workflows", wfDto, ct);
        wfResp.EnsureSuccessStatusCode();
        var wf = await wfResp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return wf!.Id;
    }

    [Fact]
    public async Task Start_DisposesSnapshot_WhenWriterThrowsAfterLoad()
    {
        var ct = TestContext.Current.CancellationToken;

        RecordingWorkflowGraphLoader? recorder = null;
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // Wrap the real loader so the populated snapshot is captured, then
                // make the LAST seam (the projection writer) throw so the using-
                // declaration's Dispose runs on the failure path.
                services.AddScoped<WorkflowGraphLoader>();
                services.AddScoped<IWorkflowGraphLoader>(sp =>
                {
                    recorder = new RecordingWorkflowGraphLoader(
                        sp.GetRequiredService<WorkflowGraphLoader>());
                    return recorder;
                });
                services.AddScoped<IRedisProjectionWriter, ThrowingRedisProjectionWriter>();
            }));

        using var client = factory.CreateClient();
        var wfId = await SeedWorkflowAsync(client, ct);

        var resp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start",
            new List<Guid> { wfId },
            ct);

        // The forced InvalidOperationException falls through to the generic 500.
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        // SC4 — the using declaration ran Dispose() on the throw path.
        Assert.NotNull(recorder);
        Assert.NotNull(recorder!.Captured);
        Assert.True(recorder.Captured!.IsDisposed);

        // Disposal cleared all 5 dictionaries (L1-BUILD-05).
        Assert.Empty(recorder.Captured.Workflows);
        Assert.Empty(recorder.Captured.Steps);
        Assert.Empty(recorder.Captured.Processors);
        Assert.Empty(recorder.Captured.Schemas);
        Assert.Empty(recorder.Captured.Assignments);
    }
}
