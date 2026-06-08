using System.Linq;
using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Contracts;

/// <summary>
/// Phase 43 Wave-0 RED Nyquist proof for SC-3 (D-11/D-12): the five Keeper-state records
/// (KeeperUpdate/KeeperReinject/KeeperInject/KeeperDelete/KeeperCleanup), each implementing the
/// IKeeperRecoverable marker that exposes EXACTLY the partition 4-tuple (corr/wf/proc/exec) and
/// deliberately NOT StepId. StepId rides as a plain property on each record (the 5-id base);
/// KeeperUpdate adds ValidatedData; KeeperReinject/KeeperDelete add EntryId.
///
/// References IKeeperRecoverable + the five records, none of which exist until Plan 02 —
/// deliberately RED until then.
/// </summary>
[Trait("Phase", "43")]
public sealed class KeeperContractTests
{
    private static readonly Type[] AllFive =
    {
        typeof(KeeperUpdate), typeof(KeeperReinject), typeof(KeeperInject),
        typeof(KeeperDelete), typeof(KeeperCleanup),
    };

    [Fact]
    public void All_five_records_implement_IKeeperRecoverable()
    {
        foreach (var t in AllFive)
            Assert.True(typeof(IKeeperRecoverable).IsAssignableFrom(t), $"{t.Name} must implement IKeeperRecoverable");
    }

    [Fact]
    public void IKeeperRecoverable_exposes_exactly_the_partition_four_tuple_and_not_StepId()
    {
        var members = typeof(IKeeperRecoverable).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var id in new[] { "CorrelationId", "WorkflowId", "ProcessorId", "ExecutionId" })
            Assert.Contains(id, members);
        // D-12: StepId is NOT part of the partition key marker.
        Assert.Null(typeof(IKeeperRecoverable).GetProperty("StepId"));
    }

    [Fact]
    public void Every_record_carries_StepId_as_a_plain_property()
    {
        // D-11: all five carry the 5-id base {corr, wf, step, proc, exec}; StepId is a record property
        // even though it is not on the IKeeperRecoverable partition marker.
        foreach (var t in AllFive)
            Assert.NotNull(t.GetProperty("StepId"));
    }

    [Fact]
    public void KeeperUpdate_carries_ValidatedData_and_no_EntryId()
    {
        Assert.NotNull(typeof(KeeperUpdate).GetProperty("ValidatedData"));   // D-11 UPDATE-only extra
        Assert.Null(typeof(KeeperUpdate).GetProperty("EntryId"));
    }

    [Fact]
    public void KeeperReinject_carries_EntryId_and_no_ValidatedData()
    {
        Assert.NotNull(typeof(KeeperReinject).GetProperty("EntryId"));   // D-11
        Assert.Null(typeof(KeeperReinject).GetProperty("ValidatedData"));
        // D-01: REINJECT carries Payload (string, init-only) so a recovered run reconstructs a
        // faithful EntryStepDispatch (the author's step config is not silently lost).
        var payload = typeof(KeeperReinject).GetProperty("Payload");
        Assert.NotNull(payload);
        Assert.Equal(typeof(string), payload!.PropertyType);
    }

    [Fact]
    public void KeeperDelete_carries_EntryId_and_no_ValidatedData()
    {
        Assert.NotNull(typeof(KeeperDelete).GetProperty("EntryId"));   // D-11
        Assert.Null(typeof(KeeperDelete).GetProperty("ValidatedData"));
    }

    [Fact]
    public void KeeperInject_carries_neither_EntryId_nor_ValidatedData()
    {
        Assert.Null(typeof(KeeperInject).GetProperty("EntryId"));
        Assert.Null(typeof(KeeperInject).GetProperty("ValidatedData"));
    }

    [Fact]
    public void KeeperCleanup_carries_neither_EntryId_nor_ValidatedData()
    {
        Assert.Null(typeof(KeeperCleanup).GetProperty("EntryId"));
        Assert.Null(typeof(KeeperCleanup).GetProperty("ValidatedData"));
    }
}
