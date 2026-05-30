using System.Net;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// CONSOLE-HEALTH-02 / D-02 fact #2 + T-18-08: the embedded listener's <c>/health/live</c> stays 200/Healthy
/// even when BOTH Redis and RabbitMQ are dead (liveness is the always-Healthy <c>self</c> check only — a
/// dependency blip can never flip liveness and trigger a pod restart, T-18-09), and no probe body leaks a
/// connection-string secret or a stack-trace marker (status-only payload via UIResponseWriter).
/// </summary>
public sealed class ConsoleHealthLiveTests : IClassFixture<ConsoleTestHostFixture>
{
    private readonly ConsoleTestHostFixture _fixture;

    public ConsoleHealthLiveTests(ConsoleTestHostFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Live_Returns_200_When_Redis_And_RabbitMQ_Dead()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _fixture.HttpClient.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Healthy\"", body);
    }

    [Fact]
    public async Task Live_Body_Has_No_Secrets()
    {
        var ct = TestContext.Current.CancellationToken;

        var liveResponse = await _fixture.HttpClient.GetAsync("/health/live", ct);
        var liveBody = await liveResponse.Content.ReadAsStringAsync(ct);

        var readyResponse = await _fixture.HttpClient.GetAsync("/health/ready", ct);
        var readyBody = await readyResponse.Content.ReadAsStringAsync(ct);

        // T-18-08: status-only body — no connection-string secrets, no stack-trace markers.
        foreach (var body in new[] { liveBody, readyBody })
        {
            Assert.DoesNotContain("Password=", body);
            Assert.DoesNotContain("password=", body);
            Assert.DoesNotContain("abortConnect", body);   // Redis connection-string token
            Assert.DoesNotContain("   at ", body);          // .NET stack-trace frame marker
        }
    }
}
