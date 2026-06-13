using System;
using BaseApi.Service.Features.Orchestration.Projection;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration.Projection;

/// <summary>
/// Phase 15 L2-PROJECT-02 (Phase 22 L2PREFIX-01 update) — verifies the flat L2 key formats
/// produced by <see cref="RedisProjectionKeys"/>. The prefix is now a compile-time const
/// ("skp:") on the shared <c>L2ProjectionKeys</c> (Phase 22 D-01), so the builders take no
/// <c>prefix</c> parameter. The scheme is prefix + GUID(s) with NO type discriminator (D-02).
/// GUIDs render in the default "D" format (hyphenated), NOT "N". The legacy flat
/// <c>Processor(Guid)</c> forwarder + its byte-identity pin were deleted in Phase 61 (D-11).
/// <c>ParentIndex()</c> returns the bare prefix (the parent-index SET key, Phase 22 D-02).
/// <see cref="RedisProjectionKeys"/> is internal — these tests rely on the existing
/// <c>InternalsVisibleTo("BaseApi.Tests")</c> on BaseApi.Service.
/// </summary>
[Trait("Phase", "22")]
public sealed class RedisProjectionKeysTests
{
    private static readonly Guid Workflow = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Step = Guid.Parse("22222222-2222-2222-2222-222222222222");

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

}
