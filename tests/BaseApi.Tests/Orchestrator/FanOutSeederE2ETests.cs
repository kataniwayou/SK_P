using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using BaseApi.Service.Features.Assignment;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using Npgsql;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Self-verifying RealStack fan-out workflow SEEDER (D-01..D-04) — the runnable artifact Phases 67/68
/// invoke via <c>dotnet test --filter "FullyQualifiedName~FanOutSeeder"</c>, and the single fact that
/// PROVES WF-01 + WF-02.
/// </summary>
/// <remarks>
/// <para>
/// Builds the v8.0.0 fan-out topology <c>A→B→C→{D1→E1→F1, D2→E2→F2}</c> against the live host stack via
/// REST only (no raw SQL inserts): 1 workflow (entry = A, cron <c>*/30 * * * * *</c>), 9 steps all bound
/// to ONE shared <c>processor-sample</c> id, exactly 8 <c>step_next_steps</c> edges, and 9 step-bound
/// assignments each carrying a <c>{ number:int, label:"Step_*" }</c> JSON payload. The single workflow's
/// <c>AssignmentIds</c> binds all 9 (two-sided binding — Pitfall 2).
/// </para>
/// <para>
/// <b>Idempotent by sentinel name (D-04).</b> The build routine GET-matches an existing workflow named
/// <c>v8-fanout-proof</c> and returns its id without creating duplicates (workflows have no unique-name
/// constraint, so the fixed sentinel name is the idempotency key — mirrors
/// <see cref="SampleRoundTripE2ETests.SeedConfigSchemaAsync"/>). The processor is resolved by-source-hash
/// GET-or-create via the reused <see cref="SampleRoundTripE2ETests.SeedProcessorAsync"/>.
/// </para>
/// <para>
/// <b>Reverse-topological create order.</b> Both <c>step_next_steps</c> FKs are <c>OnDelete(Restrict)</c>,
/// so every <c>NextStepId</c> must already exist when its parent step is created. Steps are therefore
/// created sinks-first (F1, F2 → E1, E2 → D1, D2 → C → B → A), yielding exactly the 8 edges
/// A→B, B→C, C→D1, C→D2, D1→E1, D2→E2, E1→F1, E2→F2 with F1/F2 as zero-outgoing sinks.
/// </para>
/// <para>
/// Tagged <c>Category=RealStack</c> so the hermetic filter (<c>Category!=RealStack</c>) excludes it; it
/// runs live (against a reset-clean DB brought up by the Phase-65 harness) via the
/// <c>~FanOutSeeder</c> filter. Reuses <see cref="SampleRoundTripE2ETests.RealStackWebAppFactory"/> for the
/// in-proc WebApi → host-stack overrides (Postgres 5433 / Redis 6380 / RMQ 5673 / otel 4317).
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class FanOutSeederE2ETests
{
    // Stable sentinel workflow name — the idempotency key the seed routine GET-matches on (D-04).
    private const string FanOutWorkflowName = "v8-fanout-proof";

    // 6-field seconds-cron (v8.0.0 feature) — NOT the stale 5-field linear-seed cron.
    private const string FanOutCron = "*/30 * * * * *";

    // Host Postgres connection string for the Npgsql self-verification reads (mirrors
    // SampleRoundTripE2ETests.RealStackWebAppFactory.HostPostgres :474-475).
    private const string HostPostgres =
        "Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres;Maximum Pool Size=20;Timeout=15";

    // FIXED label→number mapping (D-08). Order matters ONLY for the number assignment; create order is
    // reverse-topological (sinks first) and is driven by the explicit edge wiring below.
    private static readonly IReadOnlyDictionary<string, int> NodeNumbers = new Dictionary<string, int>
    {
        ["Step_A"] = 1,
        ["Step_B"] = 2,
        ["Step_C"] = 3,
        ["Step_D1"] = 4,
        ["Step_E1"] = 5,
        ["Step_F1"] = 6,
        ["Step_D2"] = 7,
        ["Step_E2"] = 8,
        ["Step_F2"] = 9,
    };

    /// <summary>
    /// POST one step-bound assignment carrying a <c>{ number, label }</c> JSON payload string (jsonb
    /// server-side) and return the new assignment id (Pattern 1; <see cref="AssignmentCreateDto"/>).
    /// </summary>
    internal static async Task<Guid> SeedAssignmentAsync(
        HttpClient client, Guid stepId, int number, string label, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { number, label }); // {"number":1,"label":"Step_A"}
        var dto = new AssignmentCreateDto(
            Name: $"asg-{label}-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            StepId: stepId,
            Payload: payload);
        var resp = await client.PostAsJsonAsync("/api/v1/assignments", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>
    /// Extended step seed that accepts a custom Name + <paramref name="nextStepIds"/> (the existing
    /// <see cref="SampleRoundTripE2ETests.SeedStepAsync"/> hardcodes <c>NextStepIds: null</c>). POSTs
    /// <see cref="StepCreateDto"/> with <c>StepEntryCondition.Always</c> and returns the new step id.
    /// </summary>
    internal static async Task<Guid> SeedStepWithNextAsync(
        HttpClient client, Guid processorId, string node, List<Guid>? nextStepIds, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"step-{node}-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: nextStepIds,
            EntryCondition: StepEntryCondition.Always);
        var resp = await client.PostAsJsonAsync("/api/v1/steps", dto, ct);
        resp.EnsureSuccessStatusCode();
        var read = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        return read!.Id;
    }

    /// <summary>
    /// Idempotent fan-out build routine. On a clean DB it creates exactly 1 workflow / 9 steps / 8 edges /
    /// 9 assignments and returns the new workflow id; if a workflow named <see cref="FanOutWorkflowName"/>
    /// already exists it GET-matches and returns the existing id WITHOUT creating duplicates (D-04).
    /// </summary>
    internal static async Task<Guid> SeedFanOutAsync(HttpClient client, CancellationToken ct)
    {
        // 1. Resolve the genuine processor-sample SourceHash via reflection (SampleRoundTripE2ETests:111-113)
        //    — the ONE shared processor id for all 9 steps.
        var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;
        var procId = await SampleRoundTripE2ETests.SeedProcessorAsync(client, hash, ct);

        // 2. IDEMPOTENCY GATE (mirror SeedConfigSchemaAsync :383-400) — GET-match the sentinel name.
        var all = await client.GetFromJsonAsync<List<WorkflowReadDto>>("/api/v1/workflows", ct);
        var existing = all!.FirstOrDefault(w => w.Name == FanOutWorkflowName);
        if (existing is not null)
        {
            return existing.Id; // 2nd run no-ops; id stable (WF-02 / D-04)
        }

        // 3. REVERSE-TOPOLOGICAL step create (sinks first — both step_next_steps FKs are OnDelete(Restrict),
        //    so each NextStepId must already exist). Yields exactly 8 edges:
        //    A->B, B->C, C->D1, C->D2, D1->E1, D2->E2, E1->F1, E2->F2.
        var node = new Dictionary<string, Guid>(StringComparer.Ordinal);
        node["Step_F1"] = await SeedStepWithNextAsync(client, procId, "F1", null, ct);
        node["Step_F2"] = await SeedStepWithNextAsync(client, procId, "F2", null, ct);
        node["Step_E1"] = await SeedStepWithNextAsync(client, procId, "E1", new List<Guid> { node["Step_F1"] }, ct);
        node["Step_E2"] = await SeedStepWithNextAsync(client, procId, "E2", new List<Guid> { node["Step_F2"] }, ct);
        node["Step_D1"] = await SeedStepWithNextAsync(client, procId, "D1", new List<Guid> { node["Step_E1"] }, ct);
        node["Step_D2"] = await SeedStepWithNextAsync(client, procId, "D2", new List<Guid> { node["Step_E2"] }, ct);
        node["Step_C"] = await SeedStepWithNextAsync(
            client, procId, "C", new List<Guid> { node["Step_D1"], node["Step_D2"] }, ct);
        node["Step_B"] = await SeedStepWithNextAsync(client, procId, "B", new List<Guid> { node["Step_C"] }, ct);
        node["Step_A"] = await SeedStepWithNextAsync(client, procId, "A", new List<Guid> { node["Step_B"] }, ct);

        // 4. Per node create one step-bound assignment with the FIXED number<->label mapping (D-08). The
        //    `label` is the verbatim token (e.g. "Step_A") — NO extra prefix (Phase 64 D-10).
        var assignmentIds = new List<Guid>(NodeNumbers.Count);
        foreach (var (label, number) in NodeNumbers)
        {
            var id = await SeedAssignmentAsync(client, node[label], number, label, ct);
            assignmentIds.Add(id);
        }

        // 5. Create the single workflow: entry = A only, all 9 assignments bound, 6-field seconds-cron.
        var wfDto = new WorkflowCreateDto(
            Name: FanOutWorkflowName,
            Version: "1.0.0",
            Description: null,
            EntryStepIds: new List<Guid> { node["Step_A"] },
            AssignmentIds: assignmentIds,
            CronExpression: FanOutCron);
        var wfResp = await client.PostAsJsonAsync("/api/v1/workflows", wfDto, ct);
        wfResp.EnsureSuccessStatusCode();
        var wf = await wfResp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return wf!.Id;
    }
}
