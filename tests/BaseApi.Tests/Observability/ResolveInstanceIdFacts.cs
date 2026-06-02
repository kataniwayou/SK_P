using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Phase 30 (D-10) — hermetic facts for the per-replica <c>service.instance.id</c> env-precedence
/// rule that BOTH base libs implement via a duplicated <c>private static string ResolveInstanceId()</c>
/// (D-09 — <c>BaseConsole.Core</c> is hard-forbidden from referencing <c>BaseApi.Core</c>, and the
/// helper is too small to justify a shared lib, so it is duplicated, NOT test-visible).
///
/// <para>
/// Because the production helper is <c>private static</c> and intentionally duplicated, this test
/// drives the SAME precedence expression directly through a local <see cref="Resolve"/> mirror:
/// <c>POD_NAME ?? HOSTNAME ?? Environment.MachineName ?? Guid.NewGuid().ToString("N")</c>. This
/// catches the D-10 precedence bug (logs and metrics resources disagreeing, or the wrong env var
/// winning) cheaply, without the full real stack.
/// </para>
///
/// <para>
/// Tagged <c>[Collection("Observability")]</c> so its <see cref="Environment.SetEnvironmentVariable"/>
/// mutations are serialized against the other observability tests that read shared process state
/// (HealthEndpointsTests / OrchestrationLogsE2ETests already mutate env in this collection). Every
/// fact restores POD_NAME/HOSTNAME in a finally so no test leaks process-global env.
/// </para>
/// </summary>
[Collection("Observability")]
public sealed class ResolveInstanceIdFacts
{
    /// <summary>
    /// Local mirror of the production <c>ResolveInstanceId()</c> expression (D-10). Must stay
    /// byte-for-byte equivalent to the duplicated helper in
    /// <c>ObservabilityServiceCollectionExtensions</c> and <c>BaseConsoleObservabilityExtensions</c>.
    /// </summary>
    private static string Resolve() =>
        Environment.GetEnvironmentVariable("POD_NAME")
        ?? Environment.GetEnvironmentVariable("HOSTNAME")
        ?? Environment.MachineName
        ?? Guid.NewGuid().ToString("N");

    [Fact]
    public void PodName_Set_Wins_Over_Hostname_And_MachineName()
    {
        var originalPod  = Environment.GetEnvironmentVariable("POD_NAME");
        var originalHost = Environment.GetEnvironmentVariable("HOSTNAME");
        try
        {
            Environment.SetEnvironmentVariable("POD_NAME", "pod-abc-123");
            Environment.SetEnvironmentVariable("HOSTNAME", "host-xyz");

            Assert.Equal("pod-abc-123", Resolve());   // POD_NAME is highest precedence
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

            Assert.Equal("host-xyz", Resolve());   // HOSTNAME wins when POD_NAME is unset
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

            var resolved = Resolve();

            // Never null/empty — MachineName fallback (GUID is the documented final fallback).
            Assert.False(string.IsNullOrEmpty(resolved));
            Assert.Equal(Environment.MachineName, resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAME", originalPod);
            Environment.SetEnvironmentVariable("HOSTNAME", originalHost);
        }
    }
}
