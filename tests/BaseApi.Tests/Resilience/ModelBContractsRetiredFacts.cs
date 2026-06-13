using System.Reflection;
using System.Runtime.CompilerServices;      // [CallerFilePath] (RepoRoot source-scan anchor)
using MassTransit;                          // IConsumer<> (5->3 reflection fact)
using Messaging.Contracts;                  // KeeperInject (Contracts assembly anchor)
using Messaging.Contracts.Projections;      // L2ProjectionKeys (SC-2)
using Xunit;

namespace BaseApi.Tests.Resilience;

/// <summary>
/// Phase-50 negative guards that make the v4.0.0 Model-B recovery-contract retirement (RETIRE-01/RETIRE-02)
/// self-verifying and regression-proof (D-01). The LIGHTWEIGHT in-phase SC-2 guard — the full source +
/// reflection remnant sweep (RETIRE-03) is Phase 53. Mirrors the verified <c>ReactivePathRetiredFacts</c>
/// reflection idiom verbatim, no host boot:
/// <list type="bullet">
///   <item>FACT 1 (SC-2) — REFLECTION: <c>L2ProjectionKeys</c> has no public-static method named
///     <c>CompositeBackup</c> (the retired composite-backup key builder). A re-introduction fails the build.</item>
///   <item>FACT 2 (SC-2) — TYPE ABSENCE: the <c>Messaging.Contracts</c> assembly has no type named
///     <c>KeeperUpdate</c> or <c>KeeperCleanup</c> (the retired Model-B state contracts).</item>
///   <item>FACT 3 (SC-2) — TYPE ABSENCE: the <c>Keeper</c> assembly has no type named <c>BackupOptions</c>
///     (the retired composite-backup TTL options).</item>
///   <item>FACT 4 (SC-2 positive survivor): <c>L2ProjectionKeys</c> DOES retain a single-Guid-overload
///     <c>MessageIndex</c> (the A18 slot-array index, added Plan 01) and a single-Guid-overload
///     <c>ExecutionData</c> — so the guard cannot silently pass on a wholesale builder rename.</item>
/// </list>
/// </summary>
public sealed class ModelBContractsRetiredFacts
{
    private static readonly Assembly Contracts =
        typeof(global::Messaging.Contracts.KeeperInject).Assembly;

    private static readonly Assembly Keeper =
        typeof(global::Keeper.Health.BitHealthLoop).Assembly;

    /// <summary>
    /// FACT 1 (SC-2, RETIRE-01) — REFLECTION. The retired composite-backup key builder is gone:
    /// <see cref="L2ProjectionKeys"/> exposes NO public-static method named <c>CompositeBackup</c>
    /// (a re-introduced overload of any signature trips <see cref="Assert.Empty"/>).
    /// </summary>
    [Fact]
    [Trait("Phase", "50")]
    public void L2ProjectionKeys_has_no_CompositeBackup_builder()
    {
        var compositeBackup = typeof(L2ProjectionKeys)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "CompositeBackup");

        Assert.Empty(compositeBackup);
    }

    /// <summary>
    /// FACT 2 (SC-2, RETIRE-02) — TYPE ABSENCE. The <c>Messaging.Contracts</c> assembly retains NO type named
    /// <c>KeeperUpdate</c> or <c>KeeperCleanup</c> (the retired UPDATE/CLEANUP Model-B state contracts).
    /// </summary>
    [Fact]
    [Trait("Phase", "50")]
    public void Messaging_Contracts_has_no_KeeperUpdate_or_KeeperCleanup_type()
    {
        Assert.DoesNotContain(Contracts.GetTypes(),
            t => t.Name == "KeeperUpdate" || t.Name == "KeeperCleanup");
    }

    /// <summary>
    /// FACT 3 (SC-2, RETIRE-01) — TYPE ABSENCE. The <c>Keeper</c> assembly retains NO type named
    /// <c>BackupOptions</c> (the retired composite-backup TTL options bound from the deleted "Backup" section).
    /// </summary>
    [Fact]
    [Trait("Phase", "50")]
    public void Keeper_has_no_BackupOptions_type()
    {
        Assert.DoesNotContain(Keeper.GetTypes(), t => t.Name == "BackupOptions");
    }

    /// <summary>
    /// FACT 4 (SC-2 positive survivor) — REFLECTION. <see cref="L2ProjectionKeys"/> DOES retain the A18
    /// slot-array index builder <c>MessageIndex</c> (single Guid overload, added Plan 01) and the GUID data
    /// builder <c>ExecutionData</c> (single Guid overload) — a wholesale builder rename/removal trips
    /// <see cref="Assert.Single{T}"/>, so the absence facts above cannot pass vacuously.
    /// </summary>
    [Fact]
    [Trait("Phase", "50")]
    public void L2ProjectionKeys_retains_MessageIndex_and_ExecutionData_single_guid_overloads()
    {
        AssertSingleGuidOverload("MessageIndex");
        AssertSingleGuidOverload("ExecutionData");
    }

    private static void AssertSingleGuidOverload(string name)
    {
        var overloads = typeof(L2ProjectionKeys)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == name)
            .ToList();

        var only = Assert.Single(overloads);
        var param = Assert.Single(only.GetParameters());
        Assert.Equal(typeof(Guid), param.ParameterType);
    }

    /// <summary>
    /// FACT 5 (SC-2 / RETIRE-03) — REFLECTION. The 5-state Model-B recovery surface has collapsed to
    /// EXACTLY the 3 A18 states: the Keeper assembly consumes IConsumer&lt;KeeperReinject/Inject/Delete&gt;
    /// and NO IConsumer&lt;KeeperUpdate/KeeperCleanup&gt;. Inherited closed-generic interface is reported
    /// by GetInterfaces() (RecoveryConsumerBase&lt;T&gt; : IConsumer&lt;T&gt;), no base-walk needed.
    /// </summary>
    [Fact]
    [Trait("Phase", "53")]
    public void Keeper_registers_exactly_three_recovery_consumers()
    {
        var consumed = Keeper.GetTypes()
            .SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
            .Select(i => i.GetGenericArguments()[0].Name)
            .Where(n => n is "KeeperReinject" or "KeeperInject" or "KeeperDelete"
                     or "KeeperUpdate"   or "KeeperCleanup")
            .Distinct().OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "KeeperDelete", "KeeperInject", "KeeperReinject" }, consumed);
    }

    /// <summary>
    /// FACT 6 (D-01 / SC-2) — SOURCE-SCAN. No bus-retry or error-transport CALL survives on the
    /// execution + orchestrator path. Matches the CALL pattern (endpointConfigurator/cfg.UseMessageRetry(,
    /// .ConfigureError() — NOT the bare word, which legitimately survives in ~9 doc-comments. src/ only.
    /// RED on the pre-teardown tree (the calls still exist); Wave-1 plans 02/03 turn it GREEN.
    /// </summary>
    [Fact]
    [Trait("Phase", "53")]
    public void No_bus_retry_or_error_transport_on_execution_path_endpoints()
    {
        foreach (var rel in new[] { Path.Combine("src","Orchestrator","Consumers"),
                                    Path.Combine("src","BaseProcessor.Core","Startup"),
                                    Path.Combine("src","Keeper","Recovery") })
        {
            var dir = Path.Combine(RepoRoot(), rel);
            Assert.True(Directory.Exists(dir), $"bad anchor: {dir}");
            var offenders = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var t = File.ReadAllText(f);
                    return t.Contains("endpointConfigurator.UseMessageRetry(")
                        || t.Contains("cfg.UseMessageRetry(")
                        || t.Contains(".ConfigureError(");
                })
                .ToList();
            Assert.True(offenders.Count == 0,
                "A18 end-state regressed (bus retry / error transport on exec path): "
                    + string.Join(", ", offenders));
        }
    }

    /// <summary>
    /// FACT 7 (SYMMETRIC-KEEPER-EXEC-PATH) — SOURCE-SCAN. The keeper-recovery endpoint is now SYMMETRIC with
    /// the exec path: the BaseConsole.Core global callback carries NO ConfigureError, AND the
    /// RecoveryEndpointBinder carries NEITHER ConfigureError NOR UseMessageRetry — a Guard-exhaust throw
    /// falls through to broker nack-requeue, no error transport / no bus retry anywhere on the recovery path.
    /// (This INVERTS the prior keeper-local-error assertion: the binder no longer dead-letters to skp-dlq-1.)
    /// </summary>
    [Fact]
    [Trait("Phase", "53")]
    public void Keeper_recovery_endpoint_is_symmetric_no_retry_no_error_transport()
    {
        var global = Path.Combine(RepoRoot(), "src", "BaseConsole.Core",
            "DependencyInjection", "MessagingServiceCollectionExtensions.cs");
        var binder = Path.Combine(RepoRoot(), "src", "Keeper", "Recovery", "RecoveryEndpointBinder.cs");
        Assert.True(File.Exists(global), $"bad anchor: {global}");
        Assert.True(File.Exists(binder), $"bad anchor: {binder}");

        Assert.DoesNotContain("ConfigureError", File.ReadAllText(global));   // removed from global callback

        var binderSrc = File.ReadAllText(binder);
        Assert.DoesNotContain("ConfigureError", binderSrc);    // symmetric: no error transport on the binder
        Assert.DoesNotContain("UseMessageRetry", binderSrc);   // symmetric: no bus retry on the binder
    }

    /// <summary>
    /// FACT 8 (D-07 / SC-2) — SOURCE-SCAN. The dead Ignore&lt;WorkflowRootNotFoundException&gt; (guarding a
    /// throw that never occurs) is removed from BOTH Start/Stop definitions WITH their retry block. Pure
    /// teardown: missing-root keeps log+ack; NO catch/DLQ seam is added (D-07 resolution).
    /// RED on the pre-teardown tree (both still carry it); Wave-1 turns it GREEN.
    /// </summary>
    [Fact]
    [Trait("Phase", "53")]
    public void Dead_WorkflowRootNotFound_ignore_removed_from_start_stop_definitions()
    {
        foreach (var name in new[] { "StartOrchestrationConsumerDefinition.cs",
                                     "StopOrchestrationConsumerDefinition.cs" })
        {
            var f = Path.Combine(RepoRoot(), "src", "Orchestrator", "Consumers", name);
            Assert.True(File.Exists(f), $"bad anchor: {f}");
            Assert.DoesNotContain("Ignore<WorkflowRootNotFoundException>", File.ReadAllText(f));
        }
    }

    /// <summary>
    /// Resolves the repository root from THIS source file's compile-time path (<see cref="CallerFilePathAttribute"/>):
    /// tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs -> walk up to the dir containing SK_P.sln.
    /// FALSE-PASS GUARD (T-53-01): every source-scan fact asserts Directory.Exists/File.Exists before scanning,
    /// so a mis-resolved anchor fails loudly rather than passing on a silently-empty scan.
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
