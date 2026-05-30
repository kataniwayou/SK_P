using System.Net;
using BaseConsole.Core.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// CONSOLE-HEALTH-04 / D-02 fact #3: <c>/health/startup</c> reflects the host startup gate.
/// <list type="bullet">
///   <item>200/Healthy once the host has started — <c>StartupCompletionService</c> flipped the gate.</item>
///   <item>503/Unhealthy when <c>StartupCompletionService</c> is removed by TYPE identity before Build(),
///         so the gate is never flipped (mirrors the API's HealthNoStartupCompletionFixture).</item>
/// </list>
/// </summary>
public sealed class ConsoleStartupGateTests
{
    [Fact]
    public async Task Startup_Returns_200_After_Host_Init()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new ConsoleTestHostFixture();
        await fixture.InitializeAsync();

        var response = await fixture.HttpClient.GetAsync("/health/startup", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Healthy\"", body);
    }

    [Fact]
    public async Task Startup_Returns_503_When_CompletionService_Removed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = new NoStartupCompletionConsoleFixture();
        await fixture.InitializeAsync();

        var response = await fixture.HttpClient.GetAsync("/health/startup", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Unhealthy\"", body);
    }

    /// <summary>
    /// Variant fixture: removes the <see cref="StartupCompletionService"/> hosted-service registration by
    /// Type identity BEFORE Build(), so the startup gate is never flipped and <c>/health/startup</c> stays
    /// 503/Unhealthy. Refactor-safe vs a string-name match (mirrors the API's HealthNoStartupCompletionFixture).
    /// </summary>
    private sealed class NoStartupCompletionConsoleFixture : ConsoleTestHostFixture
    {
        protected override void ConfigureBuilder(IHostApplicationBuilder builder)
        {
            base.ConfigureBuilder(builder);

            var toRemove = builder.Services
                .Where(d => d.ImplementationType == typeof(StartupCompletionService))
                .ToList();
            foreach (var d in toRemove) builder.Services.Remove(d);
        }
    }
}
