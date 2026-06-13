using System;
using Messaging.Contracts.Identity;
using Xunit;

namespace BaseApi.Tests.Identity;

/// <summary>
/// Phase 59 (KEY-03 / D-04) — hermetic facts for the shared <see cref="InstanceId.Resolve"/> SoT:
/// the POD_NAME → HOSTNAME → MachineName → GUID(N) precedence, byte-identical to the (still-duplicated,
/// deferred-dedup) observability copies. [Collection("Observability")] serializes env mutations against
/// the other env-reading tests; every fact restores POD_NAME/HOSTNAME in finally.
/// </summary>
[Trait("Phase", "59")]
[Collection("Observability")]
public sealed class InstanceIdResolverFacts
{
    [Fact]
    public void PodName_Set_Wins_Over_Hostname_And_MachineName()
    {
        var originalPod  = Environment.GetEnvironmentVariable("POD_NAME");
        var originalHost = Environment.GetEnvironmentVariable("HOSTNAME");
        try
        {
            Environment.SetEnvironmentVariable("POD_NAME", "pod-abc-123");
            Environment.SetEnvironmentVariable("HOSTNAME", "host-xyz");
            Assert.Equal("pod-abc-123", InstanceId.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAME", originalPod);
            Environment.SetEnvironmentVariable("HOSTNAME", originalHost);
        }
    }

    [Fact]
    public void PodName_Unset_Hostname_Set_Returns_Hostname()
    {
        var originalPod  = Environment.GetEnvironmentVariable("POD_NAME");
        var originalHost = Environment.GetEnvironmentVariable("HOSTNAME");
        try
        {
            Environment.SetEnvironmentVariable("POD_NAME", null);
            Environment.SetEnvironmentVariable("HOSTNAME", "host-xyz");
            Assert.Equal("host-xyz", InstanceId.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAME", originalPod);
            Environment.SetEnvironmentVariable("HOSTNAME", originalHost);
        }
    }

    [Fact]
    public void PodName_And_Hostname_Unset_Returns_NonEmpty_MachineName_Fallback()
    {
        var originalPod  = Environment.GetEnvironmentVariable("POD_NAME");
        var originalHost = Environment.GetEnvironmentVariable("HOSTNAME");
        try
        {
            Environment.SetEnvironmentVariable("POD_NAME", null);
            Environment.SetEnvironmentVariable("HOSTNAME", null);
            var resolved = InstanceId.Resolve();
            Assert.False(string.IsNullOrEmpty(resolved));
            Assert.Equal(Environment.MachineName, resolved);   // MachineName before GUID fallback
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAME", originalPod);
            Environment.SetEnvironmentVariable("HOSTNAME", originalHost);
        }
    }
}
