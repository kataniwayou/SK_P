using System.Text.Json;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Step;
using WriterStepProjection = BaseApi.Service.Features.Orchestration.Projection.StepProjection;
using BaseApi.Tests.Composition;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 15 Plan 03 integration facts for <see cref="IRedisL2Cleanup"/> against a real compose
/// Redis (via <see cref="Phase8WebAppFactory"/> + its encapsulated <c>RedisFixture</c>). Each fact
/// seeds the L2 keyspace DIRECTLY through the multiplexer (writing root JSON via
/// <see cref="WorkflowRootProjection"/>, step JSON via <see cref="StepProjection"/>, and a raw
/// processor key) so the test controls the exact graph shape, then resolves the INTERNAL
/// <see cref="IRedisL2Cleanup"/> from a DI scope (InternalsVisibleTo) and drives
/// <c>StopCleanupAsync</c>.
/// <para>
/// Proves D-06 (root + reachable per-step deletion via cycle-safe GET-and-follow), processor-key
/// retention (T-15-10 / ORCH-STOP-04 rev), cycle termination (T-15-09), dangling-step skip-tolerance
/// (T-15-11 / Pitfall 4), and absent-root no-op (Start-preclean tolerance). The per-class
/// <c>RedisFixture</c> SCAN+DEL teardown sweeps any residue — no FLUSHDB.
/// </para>
/// </summary>
[Trait("Phase", "15")]
[Collection("ParentIndex")]
public sealed class StopCleanupFacts : IClassFixture<Phase8WebAppFactory>
{
    private readonly Phase8WebAppFactory _factory;

    public StopCleanupFacts(Phase8WebAppFactory factory) => _factory = factory;

    // ---- Direct L2 seeding helpers (mirror the writer's key layout in reverse) ----
    // Phase 22 (D-24): no-prefix L2ProjectionKeys builders on the shared skp: keyspace.

    private static LivenessProjection Liveness() => new(DateTime.UtcNow, 0, "Pending");

    private static string RootKey(Guid wf) => L2ProjectionKeys.Root(wf);
    private static string StepKey(Guid wf, Guid step) => L2ProjectionKeys.Step(wf, step);
    private static string ProcKey(Guid proc) => L2ProjectionKeys.Processor(proc);

    private async Task SeedRootAsync(IDatabase db, Guid wf, List<Guid> entryStepIds, CancellationToken ct)
    {
        var root = new WorkflowRootProjection(
            EntryStepIds: entryStepIds,
            Cron: null,
            JobId: Guid.NewGuid(),
            Liveness: Liveness(),
            CorrelationId: $"corr-{Guid.NewGuid():N}");
        await db.StringSetAsync(RootKey(wf), JsonSerializer.Serialize(root));
    }

    private async Task SeedStepAsync(IDatabase db, Guid wf, Guid stepId, List<Guid> nextStepIds, CancellationToken ct)
    {
        var step = new WriterStepProjection(
            EntryCondition: StepEntryCondition.Always,
            ProcessorId: Guid.NewGuid(),
            Payload: "{}",
            NextStepIds: nextStepIds);
        await db.StringSetAsync(StepKey(wf, stepId), JsonSerializer.Serialize(step));
    }

    // ----------------------------- ORCH-STOP-04 (rev): delete root+step, keep processor -----------------------------

    [Fact]
    public async Task Stop_Deletes_Root_Step_Keeps_Processor()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = _factory.RedisMultiplexer.GetDatabase();

        var wf = Guid.NewGuid();
        var stepA = Guid.NewGuid();
        var stepB = Guid.NewGuid();
        var procId = Guid.NewGuid();

        // Root → A → B (linear). One shared processor key that MUST survive.
        await SeedRootAsync(db, wf, new List<Guid> { stepA }, ct);
        await SeedStepAsync(db, wf, stepA, new List<Guid> { stepB }, ct);
        await SeedStepAsync(db, wf, stepB, new List<Guid>(), ct);
        await db.StringSetAsync(ProcKey(procId), """{ "inputDefinition": null }""");
        // Seed the parent index so the cleanup's SREM (hoisted, D-10) is observable.
        await db.SetAddAsync(L2ProjectionKeys.ParentIndex(), wf.ToString("D"));

        // Track the processor key (the test retains it; cleanup deletes root/step itself) so the
        // shared keyspace returns to BEFORE on dispose (D-23 known-key cleanup).
        _factory.TrackRedisKey(ProcKey(procId));

        using (var scope = _factory.Services.CreateScope())
        {
            var cleanup = scope.ServiceProvider.GetRequiredService<IRedisL2Cleanup>();
            await cleanup.StopCleanupAsync(wf, ct);
        }

        Assert.False(await db.KeyExistsAsync(RootKey(wf)), "root key should be deleted");
        Assert.False(await db.KeyExistsAsync(StepKey(wf, stepA)), "step A key should be deleted");
        Assert.False(await db.KeyExistsAsync(StepKey(wf, stepB)), "step B key should be deleted");
        Assert.True(await db.KeyExistsAsync(ProcKey(procId)), "processor key must be retained");

        // L2IDX-01 / D-10: the cleanup SREMs the wf id from the shared parent index.
        var members = await db.SetMembersAsync(L2ProjectionKeys.ParentIndex());
        Assert.DoesNotContain(wf.ToString("D"), members.Select(m => m.ToString()));
    }

    // ----------------------------- D-06 / T-15-09: cyclic graph terminates -----------------------------

    [Fact]
    public async Task Stop_CyclicGraph_Terminates()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = _factory.RedisMultiplexer.GetDatabase();

        var wf = Guid.NewGuid();
        var stepA = Guid.NewGuid();
        var stepB = Guid.NewGuid();

        // Cyclic step graph A→B→A reachable from the root entryStepIds.
        await SeedRootAsync(db, wf, new List<Guid> { stepA }, ct);
        await SeedStepAsync(db, wf, stepA, new List<Guid> { stepB }, ct);
        await SeedStepAsync(db, wf, stepB, new List<Guid> { stepA }, ct);

        using (var scope = _factory.Services.CreateScope())
        {
            var cleanup = scope.ServiceProvider.GetRequiredService<IRedisL2Cleanup>();
            // Must return (not hang) despite the cycle — the visited guard terminates the walk.
            await cleanup.StopCleanupAsync(wf, ct);
        }

        Assert.False(await db.KeyExistsAsync(RootKey(wf)), "root key should be deleted");
        Assert.False(await db.KeyExistsAsync(StepKey(wf, stepA)), "step A key should be deleted");
        Assert.False(await db.KeyExistsAsync(StepKey(wf, stepB)), "step B key should be deleted");
    }

    // ----------------------------- T-15-11 / Pitfall 4: dangling step skipped -----------------------------

    [Fact]
    public async Task Stop_DanglingStep_Skipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = _factory.RedisMultiplexer.GetDatabase();

        var wf = Guid.NewGuid();
        var danglingStep = Guid.NewGuid();   // referenced by the root but never written

        // Root references a step key that does not exist — the walk must skip it, not throw.
        await SeedRootAsync(db, wf, new List<Guid> { danglingStep }, ct);

        using (var scope = _factory.Services.CreateScope())
        {
            var cleanup = scope.ServiceProvider.GetRequiredService<IRedisL2Cleanup>();
            await cleanup.StopCleanupAsync(wf, ct);   // tolerant: no throw
        }

        Assert.False(await db.KeyExistsAsync(RootKey(wf)), "root key should be deleted even with a dangling step");
        Assert.False(await db.KeyExistsAsync(StepKey(wf, danglingStep)), "dangling step key never existed");
    }

    // ----------------------------- Tolerance: absent root is a no-op (Start pre-clean) -----------------------------

    [Fact]
    public async Task Stop_AbsentRoot_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = _factory.RedisMultiplexer.GetDatabase();

        var absentWf = Guid.NewGuid();   // no root key seeded for this id

        using (var scope = _factory.Services.CreateScope())
        {
            var cleanup = scope.ServiceProvider.GetRequiredService<IRedisL2Cleanup>();
            // Absent root → tolerant no-op; must not throw.
            await cleanup.StopCleanupAsync(absentWf, ct);
        }

        Assert.False(await db.KeyExistsAsync(RootKey(absentWf)), "no root key should exist for the absent id");
    }
}
