using global::Keeper.Recovery;
using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// KEEP-09 / D-12: the keeper-recovery endpoint partitions per-key on the IKeeperRecoverable 4-tuple
/// (corr:wf:ProcessorId:executionId), deliberately EXCLUDING StepId. These facts pin the single-owner
/// <see cref="UpdateConsumerDefinition.PartitionKey"/> (the canonical string the partitioner keys on) and
/// the derived <see cref="UpdateConsumerDefinition.PartitionGuid"/> used by the 8.5.5 Guid-keyed endpoint
/// overload: two messages with the SAME 4-tuple but DIFFERENT StepId collide into the same slot (proving
/// StepId is excluded), while a different ExecutionId yields a different key.
/// <para>
/// Hermetic scope: these prove the partition KEY SHAPE (the KEEP-09 automated gate). The strict
/// cross-delivery serialization proof (UPDATE before that exec's CLEANUP/INJECT under the live partitioner)
/// defers to the Phase-49 real-stack E2E (TEST-01) per VALIDATION.md's Manual-Only row — the in-memory
/// harness cannot deterministically exercise the partitioner's slot-level ordering.
/// </para>
/// </summary>
public sealed class RecoveryPartitionFacts
{
    private static KeeperReinject Reinject(Guid corr, Guid wf, Guid proc, Guid exec, Guid step) =>
        new(wf, step, proc) { CorrelationId = corr, ExecutionId = exec, EntryId = NewGuid() };

    private static Guid NewGuid() => Guid.NewGuid();

    [Fact]
    [Trait("Phase", "46")]
    public void Partition_key_is_four_tuple_excluding_StepId()
    {
        var corr = NewGuid();
        var wf = NewGuid();
        var proc = NewGuid();
        var exec = NewGuid();

        // The canonical key is exactly the 4-tuple in :D format — no StepId, no EntryId.
        var m = Reinject(corr, wf, proc, exec, step: NewGuid());
        Assert.Equal($"{corr:D}:{wf:D}:{proc:D}:{exec:D}", UpdateConsumerDefinition.PartitionKey(m));

        // Same 4-tuple, DIFFERENT StepId → SAME key (StepId is excluded by construction).
        var sameTupleOtherStep = Reinject(corr, wf, proc, exec, step: NewGuid());
        Assert.Equal(
            UpdateConsumerDefinition.PartitionKey(m),
            UpdateConsumerDefinition.PartitionKey(sameTupleOtherStep));
        Assert.NotEqual(m.StepId, sameTupleOtherStep.StepId);   // guard: the StepIds really do differ

        // Different ExecutionId → DIFFERENT key (different exec → different partition group).
        var otherExec = Reinject(corr, wf, proc, exec: NewGuid(), step: m.StepId);
        Assert.NotEqual(
            UpdateConsumerDefinition.PartitionKey(m),
            UpdateConsumerDefinition.PartitionKey(otherExec));
    }

    [Fact]
    [Trait("Phase", "46")]
    public void Partition_guid_mirrors_the_key_shape_excluding_StepId()
    {
        var corr = NewGuid();
        var wf = NewGuid();
        var proc = NewGuid();
        var exec = NewGuid();

        var m = Reinject(corr, wf, proc, exec, step: NewGuid());

        // The derived Guid is deterministic over the key, so the same 4-tuple (any StepId) → same slot Guid.
        var sameTupleOtherStep = Reinject(corr, wf, proc, exec, step: NewGuid());
        Assert.Equal(
            UpdateConsumerDefinition.PartitionGuid(m),
            UpdateConsumerDefinition.PartitionGuid(sameTupleOtherStep));

        // Different ExecutionId → different slot Guid.
        var otherExec = Reinject(corr, wf, proc, exec: NewGuid(), step: m.StepId);
        Assert.NotEqual(
            UpdateConsumerDefinition.PartitionGuid(m),
            UpdateConsumerDefinition.PartitionGuid(otherExec));
    }
}
