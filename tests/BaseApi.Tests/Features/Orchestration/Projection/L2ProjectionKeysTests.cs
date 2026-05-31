using System;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration.Projection;

/// <summary>
/// Phase 21 HARDEN-03 — pins the hoisted shared <see cref="L2ProjectionKeys"/> output to the exact
/// byte strings the pre-refactor writer/reader produced. Mirrors the golden values in
/// <c>RedisProjectionKeysTests</c> (the writer side) so the shared source of truth is provably
/// byte-identical. The flat scheme has NO type discriminator (D-02): Root and Processor are
/// byte-identical for the same prefix+GUID; GUIDs render in the default "D" (hyphenated) format.
/// </summary>
[Trait("Phase", "21")]
public sealed class L2ProjectionKeysTests
{
    private static readonly Guid Workflow = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Step = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Processor = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Root_Produces_Prefix_Plus_HyphenatedGuid()
    {
        Assert.Equal("skp:11111111-1111-1111-1111-111111111111", L2ProjectionKeys.Root("skp:", Workflow));
    }

    [Fact]
    public void Step_Produces_Prefix_Workflow_Colon_Step()
    {
        Assert.Equal(
            "skp:11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222",
            L2ProjectionKeys.Step("skp:", Workflow, Step));
    }

    [Fact]
    public void Processor_Produces_Prefix_Plus_HyphenatedGuid()
    {
        Assert.Equal("skp:33333333-3333-3333-3333-333333333333", L2ProjectionKeys.Processor("skp:", Processor));
    }

    [Fact]
    public void Root_And_Processor_Are_ByteIdentical_For_Same_Prefix_And_Guid()
    {
        var sharedGuid = Guid.Parse("44444444-4444-4444-4444-444444444444");
        Assert.Equal(L2ProjectionKeys.Root("skp:", sharedGuid), L2ProjectionKeys.Processor("skp:", sharedGuid));
    }

    [Fact]
    public void PerClass_Test_Prefix_Composes_The_Same_Way()
    {
        const string prefix = "test:cls-abc:";
        Assert.Equal("test:cls-abc:11111111-1111-1111-1111-111111111111", L2ProjectionKeys.Root(prefix, Workflow));
        Assert.Equal(
            "test:cls-abc:11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222",
            L2ProjectionKeys.Step(prefix, Workflow, Step));
        Assert.Equal("test:cls-abc:33333333-3333-3333-3333-333333333333", L2ProjectionKeys.Processor(prefix, Processor));
    }
}
