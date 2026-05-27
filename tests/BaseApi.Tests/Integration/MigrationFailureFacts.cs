using System.Net;
using BaseApi.Tests.Composition;
using Xunit;

namespace BaseApi.Tests.Integration;

/// <summary>
/// Phase 8 D-16 verification — migration failure surfaces as failing readiness probe;
/// process does NOT crash. Closes PERSIST-10.
/// </summary>
public sealed class MigrationFailureFacts : IClassFixture<MigrationFailureWebAppFactory>
{
    private readonly MigrationFailureWebAppFactory _factory;

    public MigrationFailureFacts(MigrationFailureWebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task BootWithBadConnectionString_LeavesProcessAlive_AndStartupUnhealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        // (a) Process responsive — HEALTH-02 (no DB dep).
        var live = await client.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        // (b) Startup gate never flipped because MigrateAsync threw — HEALTH-01.
        var startup = await client.GetAsync("/health/startup", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, startup.StatusCode);

        // (c) Readiness predicate includes StartupHealthCheck (Phase 5 D-09) — HEALTH-03.
        var ready = await client.GetAsync("/health/ready", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);

        // (d) Implicit: if StartupCompletionService.StartAsync had rethrown, the test process
        // (which IS the host) would have crashed before CreateClient() returned. The fact that
        // we reached the 3 GETs at all proves the process stayed alive (PERSIST-10).
    }
}
