using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Projection;

/// <summary>
/// Pins <see cref="LivenessStatus.Healthy"/> to the exact literal "Healthy" — the single
/// source of truth (CONTRACT-03 / D-03) the processor (writer, Phase 26) and any reader share
/// so the Path-1 liveness status value cannot desync.
/// </summary>
[Trait("Phase", "25")]
public sealed class LivenessStatusTests
{
    [Fact]
    public void Healthy_Equals_Literal_Healthy()
    {
        Assert.Equal("Healthy", LivenessStatus.Healthy);
    }
}
