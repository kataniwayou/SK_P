using System.Linq;
using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Contracts;

/// <summary>
/// SC-3 (A18 / D-08/D-09): the THREE surviving Keeper-state records
/// (KeeperReinject/KeeperInject/KeeperDelete) after the Phase-50 Model-B retirement
/// (UPDATE/CLEANUP deleted). Each implements the IKeeperRecoverable marker that exposes EXACTLY the
/// partition 4-tuple (corr/wf/proc/exec) and deliberately NOT StepId. StepId rides as a plain property
/// on each record (the 5-id base). REINJECT carries EntryId + Payload; DELETE carries EntryId; INJECT
/// carries EntryId + Data (the source-delete field DeleteEntryId was dropped in Phase 70, KINJ-02).
/// </summary>
[Trait("Phase", "50")]
public sealed class KeeperContractTests
{
    private static readonly Type[] AllThree =
    {
        typeof(KeeperReinject), typeof(KeeperInject), typeof(KeeperDelete),
    };

    [Fact]
    public void All_three_records_implement_IKeeperRecoverable()
    {
        foreach (var t in AllThree)
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
        // D-11: all three carry the 5-id base {corr, wf, step, proc, exec}; StepId is a record property
        // even though it is not on the IKeeperRecoverable partition marker.
        foreach (var t in AllThree)
            Assert.NotNull(t.GetProperty("StepId"));
    }

    [Fact]
    public void KeeperReinject_carries_EntryId_and_Payload()
    {
        Assert.NotNull(typeof(KeeperReinject).GetProperty("EntryId"));   // D-09
        // D-01: REINJECT carries Payload (string, init-only) so a recovered run reconstructs a
        // faithful EntryStepDispatch (the author's step config is not silently lost).
        var payload = typeof(KeeperReinject).GetProperty("Payload");
        Assert.NotNull(payload);
        Assert.Equal(typeof(string), payload!.PropertyType);
    }

    [Fact]
    public void KeeperDelete_carries_EntryId()
    {
        Assert.NotNull(typeof(KeeperDelete).GetProperty("EntryId"));   // D-09
    }

    [Fact]
    public void KeeperInject_carries_the_reduced_id_set_EntryId_Data()
    {
        // D-08: INJECT is forward-only — it carries its own data on the envelope (no composite read).
        var entryId = typeof(KeeperInject).GetProperty("EntryId");
        Assert.NotNull(entryId);
        Assert.Equal(typeof(Guid), entryId!.PropertyType);

        var data = typeof(KeeperInject).GetProperty("Data");
        Assert.NotNull(data);
        Assert.Equal(typeof(string), data!.PropertyType);

        // KINJ-02 negative guard: the source-delete field is gone — re-adding it breaks this fact (and every
        // producer/consumer reference fails to compile).
        Assert.Null(typeof(KeeperInject).GetProperty("DeleteEntryId"));
    }
}
