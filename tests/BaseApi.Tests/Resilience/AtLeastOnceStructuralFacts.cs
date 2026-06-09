using System.Reflection;
using System.Runtime.CompilerServices;
using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Resilience;

/// <summary>
/// Standing structural guards for the v4.0.0 at-least-once + single-DLQ invariants (Phase 47, D-03).
/// Two hermetic facts, no host boot:
/// <list type="bullet">
///   <item>FACT A (R4 / RESIL-03) — reflection guard: no <c>MessageIdentity</c>/dedup machinery survives on
///     the execution-path assemblies (Orchestrator + BaseProcessor.Core). Phase-43 RETIRE-01 removed the
///     <c>H</c>/<c>flag[H]</c>/CAS dedup but left no regression guard; this is it.</item>
///   <item>FACT B (R1 structural / RESIL-02) — source-scan guard: no v4 give-up path under
///     <c>src/BaseProcessor.Core/Processing/</c> or <c>src/Keeper/Recovery/</c> references the second DLQ
///     (<see cref="KeeperQueues.DeadLetter"/> / <c>"keeper-dlq"</c>). The scan is now unconditional — the
///     dormant reactive recovery handler was deleted in the Phase-48 teardown (RETIRE-03).</item>
/// </list>
/// </summary>
public sealed class AtLeastOnceStructuralFacts
{
    // ── Execution-path assembly anchors (public sealed classes — verified loadable, firewall-test parity). ──
    private static readonly Assembly Orchestrator =
        typeof(global::Orchestrator.Dispatch.StepDispatcher).Assembly;

    private static readonly Assembly BaseProcessorCore =
        typeof(global::BaseProcessor.Core.Processing.ProcessorPipeline).Assembly;

    /// <summary>
    /// FACT A (R4, RESIL-03) — REFLECTION (NOT a string-scan; see Pitfall 2). Asserts no dedup machinery
    /// survives on either execution-path assembly: no type literally named <c>MessageIdentity</c>, and no
    /// live public/instance property or field named <c>MessageIdentity</c> (the retired dedup key).
    /// <para>
    /// WHY reflection, NOT a string-scan: the BIT health-gate carries a legitimate positional
    /// <c>string H</c> member on <c>PauseWorkflow</c>/<c>ResumeWorkflow</c> (Messaging.Contracts) — a
    /// source-scan for <c>"flag["</c> or <c>".H"</c> would false-positive on that legitimate, non-dedup
    /// member (Pitfall 2). The type/member-NAME guard sidesteps it entirely: <c>H</c> lives on the
    /// contracts assembly (NOT the execution-path assemblies reflected here), and is NOT named
    /// <c>MessageIdentity</c>, so it is correctly invisible to this guard.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Phase", "47")]
    public void No_dedup_machinery_on_execution_path()
    {
        var assemblies = new[] { Orchestrator, BaseProcessorCore };

        foreach (var asm in assemblies)
        {
            var types = asm.GetTypes();

            // No MessageIdentity TYPE survives (Phase-43 RETIRE-01 deleted it).
            Assert.DoesNotContain(types, t => t.Name == "MessageIdentity");

            // No live dedup MEMBER survives: no public/instance property or field named MessageIdentity
            // on any execution-path type (a re-introduction could resurrect the key as a member).
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            foreach (var type in types)
            {
                Assert.DoesNotContain(type.GetProperties(flags), p => p.Name == "MessageIdentity");
                Assert.DoesNotContain(type.GetFields(flags), f => f.Name == "MessageIdentity");
            }
        }
    }

    /// <summary>
    /// FACT B (R1 structural, RESIL-02) — SOURCE-SCAN. Asserts no v4 give-up path under
    /// <c>src/BaseProcessor.Core/Processing/</c> or <c>src/Keeper/Recovery/</c> references the second DLQ
    /// (<see cref="KeeperQueues.DeadLetter"/> symbol OR the <c>"keeper-dlq"</c> literal — a re-introduction
    /// could use either form). The scan is unconditional: the dormant reactive keeper-dlq sender was deleted
    /// in the Phase-48 teardown (RETIRE-03), so any file now referencing keeper-dlq is a real RESIL-02 defect
    /// (surfaced in the assertion message).
    /// <para>
    /// FALSE-PASS GUARD (T-47-01): both scoped directories are asserted to EXIST before enumerating — a
    /// silently-empty scan (wrong repo-root anchor) would be a false pass that masks a real re-introduction.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Phase", "47")]
    public void No_v4_give_up_path_references_keeper_dlq()
    {
        var repoRoot = RepoRoot();

        var processingDir = Path.Combine(repoRoot, "src", "BaseProcessor.Core", "Processing");
        var recoveryDir = Path.Combine(repoRoot, "src", "Keeper", "Recovery");

        // T-47-01 — fail loudly if the anchor resolved wrong (a silently-empty scan is a false pass).
        Assert.True(Directory.Exists(processingDir), $"Scoped dir not found (bad repo-root anchor?): {processingDir}");
        Assert.True(Directory.Exists(recoveryDir), $"Scoped dir not found (bad repo-root anchor?): {recoveryDir}");

        var offenders = Directory.EnumerateFiles(processingDir, "*.cs")
            .Concat(Directory.EnumerateFiles(recoveryDir, "*.cs"))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("KeeperQueues.DeadLetter") || text.Contains("keeper-dlq");
            })
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "v4 give-up path(s) reference the retired keeper-dlq (RESIL-02 violation): "
                + string.Join(", ", offenders));
    }

    /// <summary>
    /// Resolves the repository root from THIS source file's compile-time path (<see cref="CallerFilePathAttribute"/>):
    /// tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs -> walk up to the dir containing SK_P.sln.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SK_P.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir); // SK_P.sln must be found by walking up from this test source file.
        return dir!.FullName;
    }
}
