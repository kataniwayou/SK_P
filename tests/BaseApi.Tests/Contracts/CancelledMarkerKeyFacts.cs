using System;
using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Contracts;

/// <summary>
/// Phase 32 Wave-1 (req-2 foundation, D-02/D-07). Pins the exact shape of the no-TTL in-flight
/// cancellation marker key + its shared sentinel value, BEFORE any writer/reader uses them. The
/// <see cref="L2ProjectionKeys.Cancelled"/> builder is the single source of truth consumed by the
/// processor SET site (Plan 04) and both consumer CHECK sites (Plan 03); the
/// <see cref="L2ProjectionKeys.CancelledMarkerValue"/> const is the single sentinel literal used at
/// both set and check sites — so a format/value change cannot desync writer/reader.
/// <para>
/// The shape mirrors the <see cref="L2ProjectionKeys.Root"/> <c>:D</c> (hyphenated) precedent (NOT the
/// 64-hex content-addressed <c>data</c>/<c>flag</c> keys). Pinning the exact <c>skp:cancelled:{id:D}</c>
/// string also lets the Plan-05 close-gate teardown scan <c>skp:cancelled:*</c> (the no-TTL keys won't
/// self-expire). Hermetic (default Category) — no real stack.
/// </para>
/// </summary>
public sealed class CancelledMarkerKeyFacts
{
    [Fact]
    public void Cancelled_Produces_Prefix_Cancelled_Discriminator_Plus_HyphenatedGuid()
    {
        var workflowId = Guid.Parse("77777777-7777-7777-7777-777777777777");

        Assert.Equal(
            "skp:cancelled:77777777-7777-7777-7777-777777777777",
            L2ProjectionKeys.Cancelled(workflowId));
    }

    [Fact]
    public void Cancelled_Matches_The_Interpolated_D_Format_For_Any_Guid()
    {
        var workflowId = Guid.NewGuid();

        Assert.Equal($"skp:cancelled:{workflowId:D}", L2ProjectionKeys.Cancelled(workflowId));
    }

    [Fact]
    public void Cancelled_Is_Distinct_From_Root_And_Processor()
    {
        var g = Guid.Parse("88888888-8888-8888-8888-888888888888");

        Assert.NotEqual(L2ProjectionKeys.Root(g), L2ProjectionKeys.Cancelled(g));
        Assert.NotEqual(L2ProjectionKeys.Processor(g), L2ProjectionKeys.Cancelled(g));
    }

    [Fact]
    public void CancelledMarkerValue_Is_The_Pinned_Sentinel()
    {
        Assert.Equal("true", L2ProjectionKeys.CancelledMarkerValue);
    }
}
