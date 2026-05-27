using System.Net;
using BaseApi.Tests.Composition;
using Xunit;

namespace BaseApi.Tests.Swagger;

/// <summary>HTTP-16 + SC#4 — Swagger UI accessible in Development, 404 in Production.</summary>
public sealed class SwaggerEnvironmentFacts
{
    [Fact]
    public async Task SwaggerDocV1_Returns_200_In_Development()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new Phase7WebAppFactory();  // default env is Development
        await factory.InitializeAsync();
        using var client  = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerUi_Returns_200_In_Development()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new Phase7WebAppFactory();
        await factory.InitializeAsync();
        using var client  = factory.CreateClient();

        var response = await client.GetAsync("/swagger", ct);

        // /swagger redirects to /swagger/index.html under default config; either 200 or 30x is acceptable.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.MovedPermanently ||
            response.StatusCode == HttpStatusCode.Redirect,
            $"Expected 200/301/302 in Dev, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task SwaggerUi_Returns_404_In_Production()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ProductionWebAppFactory();
        // ProductionWebAppFactory does NOT inherit Phase7WebAppFactory; it only flips the env to
        // Production for the /swagger 404 contract. No Postgres throwaway DB needed because the
        // /swagger probe never reaches a controller (it returns 404 from routing in Prod).
        using var client  = factory.CreateClient();

        var response = await client.GetAsync("/swagger", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerDocV1_Returns_404_In_Production()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ProductionWebAppFactory();
        // ProductionWebAppFactory does NOT inherit Phase7WebAppFactory; it only flips the env to
        // Production for the /swagger 404 contract. No Postgres throwaway DB needed because the
        // /swagger probe never reaches a controller (it returns 404 from routing in Prod).
        using var client  = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
