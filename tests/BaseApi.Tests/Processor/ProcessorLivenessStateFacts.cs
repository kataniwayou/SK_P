using BaseProcessor.Core.Liveness;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// L1-01 / D-08/D-09/D-10 (Phase 60): the dedicated in-memory liveness holder
/// <see cref="ProcessorLivenessState"/> stores a single volatile immutable
/// <see cref="ProcessorLivenessEntry"/> reference, updated by BOTH loops and read by the
/// Phase-61 self-watchdog probe. Pure-hermetic — no Redis, no harness. Status asserted via the
/// <see cref="LivenessStatus"/> const, never a literal string.
/// </summary>
[Trait("Phase", "60")]
public sealed class ProcessorLivenessStateFacts
{
    [Fact]
    public void Current_Is_Null_Before_First_Update()
    {
        var state = new ProcessorLivenessState();
        Assert.Null(state.Current);
    }

    [Fact]
    public void Update_Publishes_Last_Entry() // L1-01 / D-09: SAME immutable reference
    {
        var state = new ProcessorLivenessState();
        var entry = ProcessorLivenessEntry.Create(
            SchemaOutcome.Success, SchemaOutcome.Success, SchemaOutcome.Success,
            DateTime.UtcNow, interval: 10);

        state.Update(entry);

        Assert.Same(entry, state.Current);
    }

    [Fact]
    public void Update_Overwrites_With_Latest() // both loops update — last write wins
    {
        var state = new ProcessorLivenessState();
        var unhealthy = ProcessorLivenessEntry.Create(
            SchemaOutcome.Fail, SchemaOutcome.Success, SchemaOutcome.Success,
            DateTime.UtcNow, interval: 30);
        var healthy = ProcessorLivenessEntry.Create(
            SchemaOutcome.Success, SchemaOutcome.Success, SchemaOutcome.Success,
            DateTime.UtcNow, interval: 10);

        state.Update(unhealthy);
        state.Update(healthy);

        Assert.Same(healthy, state.Current);
        Assert.Equal(LivenessStatus.Healthy, state.Current!.Status); // const, never a literal
    }
}
