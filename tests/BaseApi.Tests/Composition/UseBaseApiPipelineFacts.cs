using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>HTTP-14 — UseBaseApi pipeline order. /api/v1/tests goes through
/// CorrelationIdMiddleware (response header echoes).</summary>
public sealed class UseBaseApiPipelineFacts
{
    [Fact]
    public async Task Probe_ApiV1Tests_Returns_XCorrelationId_Header()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new Phase7WebAppFactory();
        await factory.InitializeAsync();
        using var client  = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/tests", ct);

        // Empty list returns 200 (TestEntity DbSet empty in the throwaway DB).
        // X-Correlation-Id header MUST be present regardless of status code.
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var values));
        var corr = values!.First();
        Assert.Matches("^[a-f0-9]{32}$", corr);
    }
}
