using System.Text.Json;
using global::Keeper.Recovery;
using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Contracts;

/// <summary>
/// ORCV-06 (Wave 2 / D-06/D-07): the two origin-split orchestrator keeper-recovery contracts
/// (<see cref="OrchestratorInject"/> / <see cref="OrchestratorReinject"/>). Mirrors
/// <see cref="KeeperContractTests"/> for the IKeeperRecoverable assertion and adds the two
/// orchestrator-specific facts: (1) OrchestratorReinject round-trips its <see cref="StepOutcome"/>
/// discriminator plus the populated union field (ErrorMessage on Failed, CancellationMessage on
/// Cancelled) under default System.Text.Json; (2) <see cref="ReinjectConsumerDefinition.PartitionGuid"/>
/// is stable and origin-agnostic — two instances of the two DIFFERENT new contract types sharing the
/// same corr:wf:proc:exec 4-tuple partition to the SAME slot (proving the existing helper needs no
/// change for the new origin).
/// </summary>
[Trait("Phase", "71")]
public sealed class OrchestratorContractTests
{
    [Fact]
    public void OrchestratorInject_implements_IKeeperRecoverable()
    {
        var m = new OrchestratorInject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.IsAssignableFrom<IKeeperRecoverable>(m);
    }

    [Fact]
    public void OrchestratorReinject_implements_IKeeperRecoverable()
    {
        var m = new OrchestratorReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.IsAssignableFrom<IKeeperRecoverable>(m);
    }

    [Fact]
    public void OrchestratorReinject_roundtrips_outcome_and_union_fields()
    {
        // Failed: Outcome + ErrorMessage must survive a default-STJ round trip.
        var failed = new OrchestratorReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            Outcome = StepOutcome.Failed,
            ErrorMessage = "boom",
        };
        var failedBack = JsonSerializer.Deserialize<OrchestratorReinject>(JsonSerializer.Serialize(failed));
        Assert.NotNull(failedBack);
        Assert.Equal(StepOutcome.Failed, failedBack!.Outcome);
        Assert.Equal("boom", failedBack.ErrorMessage);
        Assert.Null(failedBack.CancellationMessage);

        // Cancelled: Outcome + CancellationMessage must survive a default-STJ round trip.
        var cancelled = new OrchestratorReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            Outcome = StepOutcome.Cancelled,
            CancellationMessage = "stopped",
        };
        var cancelledBack = JsonSerializer.Deserialize<OrchestratorReinject>(JsonSerializer.Serialize(cancelled));
        Assert.NotNull(cancelledBack);
        Assert.Equal(StepOutcome.Cancelled, cancelledBack!.Outcome);
        Assert.Equal("stopped", cancelledBack.CancellationMessage);
        Assert.Null(cancelledBack.ErrorMessage);
    }

    [Fact]
    public void PartitionGuid_is_stable_and_origin_agnostic()
    {
        // SAME corr:wf:proc:exec 4-tuple across the TWO different new contract types → SAME partition slot.
        var corr = Guid.NewGuid();
        var wf = Guid.NewGuid();
        var proc = Guid.NewGuid();
        var exec = Guid.NewGuid();

        var inject = new OrchestratorInject(wf, Guid.NewGuid(), proc)
        {
            CorrelationId = corr,
            ExecutionId = exec,
            EntryId = Guid.NewGuid(),
            OriginEntryId = Guid.NewGuid(),
        };
        var reinject = new OrchestratorReinject(wf, Guid.NewGuid(), proc)
        {
            CorrelationId = corr,
            ExecutionId = exec,
            Outcome = StepOutcome.Completed,
            EntryId = Guid.NewGuid(),
        };

        // Stable: same instance → same Guid.
        Assert.Equal(
            ReinjectConsumerDefinition.PartitionGuid(inject),
            ReinjectConsumerDefinition.PartitionGuid(inject));

        // Origin-agnostic: same 4-tuple, different type/StepId/origin → same partition slot (no helper change).
        Assert.Equal(
            ReinjectConsumerDefinition.PartitionGuid(inject),
            ReinjectConsumerDefinition.PartitionGuid(reinject));

        // Different ExecutionId → different slot (sanity guard that the key actually varies).
        var otherExec = new OrchestratorInject(wf, inject.StepId, proc)
        {
            CorrelationId = corr,
            ExecutionId = Guid.NewGuid(),
            EntryId = inject.EntryId,
            OriginEntryId = inject.OriginEntryId,
        };
        Assert.NotEqual(
            ReinjectConsumerDefinition.PartitionGuid(inject),
            ReinjectConsumerDefinition.PartitionGuid(otherExec));
    }
}
