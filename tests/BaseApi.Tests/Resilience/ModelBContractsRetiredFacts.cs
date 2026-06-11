using System.Reflection;
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
}
