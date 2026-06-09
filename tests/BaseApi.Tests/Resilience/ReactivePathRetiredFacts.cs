using System.Reflection;
using System.Runtime.CompilerServices;
using MassTransit;                          // IConsumer<>, Fault<>
using Messaging.Contracts;                  // KeeperQueues
using Messaging.Contracts.Projections;      // L2ProjectionKeys (SC-2)
using Xunit;

namespace BaseApi.Tests.Resilience;

/// <summary>
/// Phase-48 negative guards that make the v3.x reactive-recovery teardown (RETIRE-03) self-verifying
/// and regression-proof (D-01 / T-48-01). Four hermetic facts, no host boot — mirrors the verified
/// <c>AtLeastOnceStructuralFacts</c> reflection + source-scan idiom verbatim:
/// <list type="bullet">
///   <item>FACT 1 (SC-3) — REFLECTION: no <c>Fault&lt;T&gt;</c> consumer (and no <c>KeeperRecoveryHandler</c>)
///     survives on the Keeper assembly. A re-introduced reactive consumer fails the build.</item>
///   <item>FACT 2 (SC-3) — SOURCE-SCAN: no retired reactive / keeper-dlq literal
///     (<c>"keeper-fault-recovery"</c> / <c>"keeper-dlq"</c> / <c>KeeperQueues.FaultRecovery</c> /
///     <c>KeeperQueues.DeadLetter</c>) is reachable anywhere under <c>src/Keeper/</c> (recursive).</item>
///   <item>FACT 3 (SC-3) — CONST ABSENCE: <c>KeeperQueues</c> has no <c>FaultRecovery</c>/<c>DeadLetter</c>
///     field and DOES retain <c>Recovery</c> (the sole surviving Keeper queue).</item>
///   <item>FACT 4 (SC-2, RETIRE-02 remnant-verify): <c>L2ProjectionKeys.ExecutionData</c> has exactly one
///     overload whose single param is a <c>Guid</c>, and no execution-path type name contains
///     <c>"Manifest"</c> — the lightest falsifiable proof of "L2 data is the GUID entryId scheme only".</item>
/// </list>
/// </summary>
public sealed class ReactivePathRetiredFacts
{
    // ── Assembly anchor — the SAME surviving type the firewall test was re-anchored to in Plan 01
    //    (NOT a Consumers.Fault* type — those were deleted in the RETIRE-03 teardown). ──
    private static readonly Assembly Keeper =
        typeof(global::Keeper.Health.BitHealthLoop).Assembly;

    // ── Execution-path assembly anchors (same pattern AtLeastOnceStructuralFacts uses) — SC-2 no-Manifest. ──
    private static readonly Assembly Orchestrator =
        typeof(global::Orchestrator.Dispatch.StepDispatcher).Assembly;

    private static readonly Assembly BaseProcessorCore =
        typeof(global::BaseProcessor.Core.Processing.ProcessorPipeline).Assembly;

    /// <summary>
    /// FACT 1 (SC-3) — REFLECTION (NOT a string-scan). Asserts the reactive <c>Fault&lt;T&gt;</c> recovery
    /// surface is gone from the Keeper assembly: no type literally named after the deleted consumers /
    /// handler, AND (the stronger interface-shape check) no surviving type implements an
    /// <c>IConsumer&lt;Fault&lt;T&gt;&gt;</c> — a re-introduction under a different name still trips it.
    /// </summary>
    [Fact]
    [Trait("Phase", "48")]
    public void No_reactive_fault_consumer_survives_on_keeper_assembly()
    {
        var types = Keeper.GetTypes();

        // No retired type NAME survives (the deleted reactive consumers + handler).
        Assert.DoesNotContain(types, t =>
            t.Name is "FaultEntryStepDispatchConsumer"
                   or "FaultExecutionResultConsumer"
                   or "KeeperRecoveryHandler");

        // Stronger interface-shape check: no surviving type implements IConsumer<Fault<T>> for any T —
        // a reactive recovery consumer re-introduced under ANY name is caught here.
        Assert.DoesNotContain(types, t =>
            t.GetInterfaces().Any(i =>
                i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IConsumer<>)
                && i.GetGenericArguments()[0].IsGenericType
                && i.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(Fault<>)));
    }

    /// <summary>
    /// FACT 2 (SC-3) — SOURCE-SCAN over ALL of <c>src/Keeper/</c> (recursive). Asserts no retired reactive /
    /// keeper-dlq literal is reachable: <c>"keeper-fault-recovery"</c>, <c>"keeper-dlq"</c>,
    /// <c>KeeperQueues.FaultRecovery</c>, or <c>KeeperQueues.DeadLetter</c>. No <c>KeeperRecoveryHandler.cs</c>
    /// exclusion — that file was deleted in Plan 01.
    /// <para>
    /// FALSE-PASS GUARD (T-47-01 / Pitfall 5): the scoped directory is asserted to EXIST before enumerating —
    /// a silently-empty scan (wrong repo-root anchor) would be a false pass that masks a re-introduction.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Phase", "48")]
    public void No_retired_reactive_literal_under_src_keeper()
    {
        var keeperDir = Path.Combine(RepoRoot(), "src", "Keeper");

        // T-47-01 — fail loudly if the anchor resolved wrong (a silently-empty scan is a false pass).
        Assert.True(Directory.Exists(keeperDir), $"Scoped dir not found (bad repo-root anchor?): {keeperDir}");

        var offenders = Directory.EnumerateFiles(keeperDir, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("keeper-fault-recovery")
                    || text.Contains("keeper-dlq")
                    || text.Contains("KeeperQueues.FaultRecovery")
                    || text.Contains("KeeperQueues.DeadLetter");
            })
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "retired reactive/keeper-dlq path re-introduced (RETIRE-03 violation): "
                + string.Join(", ", offenders));
    }

    /// <summary>
    /// FACT 3 (SC-3) — CONST ABSENCE (reflection over <see cref="KeeperQueues"/>). Asserts the retired
    /// <c>FaultRecovery</c> (keeper-fault-recovery) + <c>DeadLetter</c> (keeper-dlq) consts are gone and the
    /// sole surviving <c>Recovery</c> queue const remains.
    /// </summary>
    [Fact]
    [Trait("Phase", "48")]
    public void KeeperQueues_has_only_recovery_const()
    {
        var fields = typeof(KeeperQueues).GetFields(BindingFlags.Public | BindingFlags.Static);

        Assert.DoesNotContain(fields, f => f.Name == "FaultRecovery");
        Assert.DoesNotContain(fields, f => f.Name == "DeadLetter");
        Assert.Contains(fields, f => f.Name == "Recovery");
    }

    /// <summary>
    /// FACT 4 (SC-2, RETIRE-02 remnant-verify) — REFLECTION. The lightest falsifiable proof of the literal
    /// SC-2 wording ("L2 data is the GUID entryId scheme only"): <see cref="L2ProjectionKeys.ExecutionData"/>
    /// has exactly ONE overload and its single parameter is a <see cref="Guid"/> (a re-introduced
    /// string/64-hex content-addressed overload trips <see cref="Assert.Single{T}"/>), AND no type named
    /// <c>*Manifest*</c> survives on the execution-path assemblies (a resurrected result manifest trips it).
    /// </summary>
    [Fact]
    [Trait("Phase", "48")]
    public void ExecutionData_is_guid_only_and_no_manifest_type_survives()
    {
        // SC-2 (a): ExecutionData is the SOLE GUID-keyed data builder — exactly one overload, param is Guid.
        var executionDataOverloads = typeof(L2ProjectionKeys)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "ExecutionData")
            .ToList();

        var only = Assert.Single(executionDataOverloads);
        var param = Assert.Single(only.GetParameters());
        Assert.Equal(typeof(Guid), param.ParameterType);

        // SC-2 (b): no result-manifest type survives on the execution-path assemblies (RETIRE-02).
        foreach (var asm in new[] { Orchestrator, BaseProcessorCore })
        {
            Assert.DoesNotContain(asm.GetTypes(), t => t.Name.Contains("Manifest"));
        }
    }

    /// <summary>
    /// Resolves the repository root from THIS source file's compile-time path (<see cref="CallerFilePathAttribute"/>):
    /// tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs -> walk up to the dir containing SK_P.sln.
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
