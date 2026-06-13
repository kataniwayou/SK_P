using System.Net;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// Phase 61 / PROBE-01/02 (D-01/02/03/04) — the END-TO-END integration proof that <c>AddBaseProcessor</c>
/// surfaces the Plan-02 self-watchdog on the embedded <c>/health/live</c> listener and that the per-schema
/// summary actually rides into the response body. Plan 02 proved the watchdog verdict in isolation (pure
/// unit over a fabricated <c>IProcessorLivenessState</c>); this class drives the FULL wiring path through the
/// real embedded Kestrel listener:
/// <list type="bullet">
///   <item>stale/null L1 ⇒ the aggregate flips Unhealthy ⇒ HTTP 503 (the default HealthCheckOptions maps an
///   Unhealthy aggregate to ServiceUnavailable) — PROBE-01.</item>
///   <item>fresh L1 ⇒ 200 Healthy AND the body carries inputSchema/outputSchema/configSchema — PROBE-02.</item>
///   <item>the body leaks no connection-string token or stack frame (T-61-07, mirrors
///   <see cref="ConsoleHealthLiveTests"/>.Live_Body_Has_No_Secrets / T-18-08).</item>
/// </list>
///
/// <para>
/// <b>Shared-fixture isolation.</b> <c>IClassFixture</c> shares ONE fixture instance across all facts and
/// xUnit runs them in nondeterministic order, so a fresh seed from one fact would leak into the null case.
/// The null fact therefore lives in its OWN class against a dedicated <see cref="NeverSeedingFixture"/> that
/// is constructed but NEVER seeded — its embedded listener only ever sees <c>Current == null</c>. The
/// stale/fresh/no-secret facts share the seeding <see cref="ProcessorConsoleTestHostFixture"/> and each
/// re-seeds immediately before its GET (the watchdog re-resolves <c>Current</c> on every check, so a
/// re-seed-then-GET within one fact is deterministic regardless of sibling order).
/// </para>
/// </summary>
[Trait("Phase", "61")]
public sealed class ProcessorHealthLiveTests : IClassFixture<ProcessorConsoleTestHostFixture>
{
    private readonly ProcessorConsoleTestHostFixture _fixture;

    public ProcessorHealthLiveTests(ProcessorConsoleTestHostFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Live_Is_Unhealthy_When_L1_Stale()
    {
        var ct = TestContext.Current.CancellationToken;

        // now far past Timestamp + Interval*2 (interval 0 => any past timestamp is stale).
        _fixture.SeedLiveness(DateTime.UtcNow.AddDays(-1), 0);

        var response = await _fixture.HttpClient.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Unhealthy\"", body);
    }

    [Fact]
    public async Task Live_Is_Healthy_And_Carries_Summary_When_L1_Fresh()
    {
        var ct = TestContext.Current.CancellationToken;

        _fixture.SeedLiveness(DateTime.UtcNow, 300);

        var response = await _fixture.HttpClient.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // PROBE-02: the per-schema summary rides into the body via the listener's per-check data writer.
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("inputSchema", body);
        Assert.Contains("outputSchema", body);
        Assert.Contains("configSchema", body);
    }

    [Fact]
    public async Task Live_Body_Has_No_Secrets()
    {
        var ct = TestContext.Current.CancellationToken;

        _fixture.SeedLiveness(DateTime.UtcNow, 300);

        var response = await _fixture.HttpClient.GetAsync("/health/live", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // T-61-07: status + summary only — no connection-string secret, no stack-trace frame.
        Assert.DoesNotContain("Password=", body);
        Assert.DoesNotContain("abortConnect", body);   // Redis connection-string token
        Assert.DoesNotContain("   at ", body);          // .NET stack-trace frame marker
    }
}

/// <summary>
/// Phase 61 / PROBE-01 (D-02) — the isolated null-verdict proof. Lives in its OWN class against a fixture
/// that is NEVER seeded, so <c>IProcessorLivenessState.Current</c> stays null and the watchdog reports
/// "liveness loop not started" ⇒ Unhealthy ⇒ 503. Isolating the null case in a separate class fixture
/// prevents a fresh seed from a sibling fact leaking into the null state under shared-fixture ordering.
/// </summary>
[Trait("Phase", "61")]
public sealed class ProcessorHealthLiveNullTests
    : IClassFixture<ProcessorHealthLiveNullTests.NeverSeedingFixture>
{
    private readonly NeverSeedingFixture _fixture;

    public ProcessorHealthLiveNullTests(NeverSeedingFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Live_Is_Unhealthy_When_L1_Null()
    {
        var ct = TestContext.Current.CancellationToken;

        // Do NOT seed — Current is null (the loop crashed before its first write, D-02).
        var response = await _fixture.HttpClient.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Unhealthy\"", body);
    }

    /// <summary>A processor fixture that is constructed but never seeded — Current stays null.</summary>
    public sealed class NeverSeedingFixture : ProcessorConsoleTestHostFixture;
}
