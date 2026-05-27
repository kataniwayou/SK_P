using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>HTTP-14 — UseBaseApi pipeline order. /api/v1/tests goes through
/// CorrelationIdMiddleware (response header echoes).
///
/// <para>
/// IN-05: hoists Phase7WebAppFactory to a class fixture (one throwaway Postgres DB per
/// test class instead of one per fact). xUnit invokes IAsyncLifetime.InitializeAsync on
/// the shared instance before any fact runs.
/// </para>
/// </summary>
public sealed class UseBaseApiPipelineFacts : IClassFixture<Phase7WebAppFactory>
{
    private readonly Phase7WebAppFactory _factory;

    public UseBaseApiPipelineFacts(Phase7WebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Probe_ApiV1Tests_Returns_XCorrelationId_Header()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/tests", ct);

        // Empty list returns 200 (TestEntity DbSet empty in the throwaway DB).
        // X-Correlation-Id header MUST be present regardless of status code.
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var values));
        var corr = values!.First();
        Assert.Matches("^[a-f0-9]{32}$", corr);
    }
}
