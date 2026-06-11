using System;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration.Projection;

/// <summary>
/// Pins the shared <see cref="L2ProjectionKeys"/> output to the exact byte strings the writer/reader
/// produce. Phase 22 (D-01/D-02) made the prefix a compile-time <c>const Prefix = "skp:"</c> and added
/// <see cref="L2ProjectionKeys.ParentIndex"/> (the bare-prefix parent-index SET key); all builders are now
/// parameterless. The flat scheme has NO type discriminator (D-02): Root and Processor are byte-identical
/// for the same GUID; GUIDs render in the default "D" (hyphenated) format.
/// </summary>
[Trait("Phase", "22")]
public sealed class L2ProjectionKeysTests
{
    private static readonly Guid Workflow = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Step = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Processor = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void ParentIndex_Returns_Bare_Prefix()
    {
        Assert.Equal("skp:", L2ProjectionKeys.ParentIndex());
    }

    [Fact]
    public void Root_Produces_Prefix_Plus_HyphenatedGuid()
    {
        Assert.Equal("skp:11111111-1111-1111-1111-111111111111", L2ProjectionKeys.Root(Workflow));
    }

    [Fact]
    public void Step_Produces_Prefix_Workflow_Colon_Step()
    {
        Assert.Equal(
            "skp:11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222",
            L2ProjectionKeys.Step(Workflow, Step));
    }

    [Fact]
    public void Processor_Produces_Prefix_Plus_HyphenatedGuid()
    {
        Assert.Equal("skp:33333333-3333-3333-3333-333333333333", L2ProjectionKeys.Processor(Processor));
    }

    [Fact]
    public void Root_And_Processor_Are_ByteIdentical_For_Same_Guid()
    {
        var sharedGuid = Guid.Parse("44444444-4444-4444-4444-444444444444");
        Assert.Equal(L2ProjectionKeys.Root(sharedGuid), L2ProjectionKeys.Processor(sharedGuid));
    }

    [Fact]
    public void ExecutionData_Produces_Prefix_Data_Discriminator_Plus_HyphenatedGuid()
    {
        Assert.Equal(
            "skp:data:55555555-5555-5555-5555-555555555555",
            L2ProjectionKeys.ExecutionData(Guid.Parse("55555555-5555-5555-5555-555555555555")));
    }

    [Fact]
    public void MessageIndex_Produces_Prefix_Msg_Discriminator_Plus_HyphenatedGuid()
    {
        Assert.Equal(
            "skp:msg:55555555-5555-5555-5555-555555555555",
            L2ProjectionKeys.MessageIndex(Guid.Parse("55555555-5555-5555-5555-555555555555")));
    }

    [Fact]
    public void ExecutionData_Is_Distinct_From_Root_And_Processor()
    {
        var g = Guid.Parse("66666666-6666-6666-6666-666666666666");
        Assert.NotEqual(L2ProjectionKeys.Root(g), L2ProjectionKeys.ExecutionData(g));
        Assert.NotEqual(L2ProjectionKeys.Processor(g), L2ProjectionKeys.ExecutionData(g));
    }

    // Phase-50 (D-01): the Model-B composite-backup key builder is RETIRED (RETIRE-01) — its golden pin
    // is removed; ModelBContractsRetiredFacts now reflection-proves its absence.

    // D-08: ExecutionData's sole overload after Plan 02 is the Guid one — pin skp:data:{guid:D}.
    [Fact]
    public void ExecutionData_Guid_Overload_Pins_skp_data_HyphenatedGuid()
        => Assert.Equal("skp:data:55555555-5555-5555-5555-555555555555",
            L2ProjectionKeys.ExecutionData(Guid.Parse("55555555-5555-5555-5555-555555555555")));
}
