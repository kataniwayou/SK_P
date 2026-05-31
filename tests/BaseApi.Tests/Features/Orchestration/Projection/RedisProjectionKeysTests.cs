using System;
using BaseApi.Service.Features.Orchestration.Projection;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration.Projection;

/// <summary>
/// Phase 15 L2-PROJECT-02 (Phase 22 L2PREFIX-01 update) — verifies the flat L2 key formats
/// produced by <see cref="RedisProjectionKeys"/>. The prefix is now a compile-time const
/// ("skp:") on the shared <c>L2ProjectionKeys</c> (Phase 22 D-01), so the builders take no
/// <c>prefix</c> parameter. The scheme is prefix + GUID(s) with NO type discriminator: Root
/// and Processor are byte-identical for the same Guid (D-02 — disambiguated only by GUID
/// namespace). GUIDs render in the default "D" format (hyphenated), NOT "N".
/// <c>ParentIndex()</c> returns the bare prefix (the parent-index SET key, Phase 22 D-02).
/// <see cref="RedisProjectionKeys"/> is internal — these tests rely on the existing
/// <c>InternalsVisibleTo("BaseApi.Tests")</c> on BaseApi.Service.
/// </summary>
[Trait("Phase", "22")]
public sealed class RedisProjectionKeysTests
{
    private static readonly Guid Workflow = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Step = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Processor = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void ParentIndex_Produces_Bare_Prefix()
    {
        Assert.Equal("skp:", RedisProjectionKeys.ParentIndex());
    }

    [Fact]
    public void Root_Produces_Prefix_Plus_HyphenatedGuid()
    {
        var key = RedisProjectionKeys.Root(Workflow);
        Assert.Equal("skp:11111111-1111-1111-1111-111111111111", key);
    }

    [Fact]
    public void Step_Produces_Prefix_Workflow_Colon_Step()
    {
        var key = RedisProjectionKeys.Step(Workflow, Step);
        Assert.Equal(
            "skp:11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222",
            key);
    }

    [Fact]
    public void Processor_Produces_Prefix_Plus_HyphenatedGuid()
    {
        var key = RedisProjectionKeys.Processor(Processor);
        Assert.Equal("skp:33333333-3333-3333-3333-333333333333", key);
    }

    [Fact]
    public void Root_And_Processor_Are_ByteIdentical_For_Same_Guid()
    {
        // Flat single-prefix scheme has NO discriminator (D-02): Root and Processor
        // formats are intentionally identical, disambiguated only by GUID namespace.
        var sharedGuid = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var root = RedisProjectionKeys.Root(sharedGuid);
        var processor = RedisProjectionKeys.Processor(sharedGuid);
        Assert.Equal(root, processor);
    }
}
