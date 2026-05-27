using System.Net;
using BaseApi.Tests.Composition;
using Xunit;

namespace BaseApi.Tests.Swagger;

/// <summary>HTTP-16 + SC#4 — Swagger UI accessible in Development, 404 in Production.
///
/// <para>
/// IN-05: hoists Phase7WebAppFactory (the throwaway-DB-creating one) to a class fixture
/// so the two Development facts share a single Postgres DB. The two Production facts keep
/// per-fact ProductionWebAppFactory because that class is internal sealed (no DB lifecycle)
/// and IClassFixture requires a public type.
/// </para>
/// </summary>
public sealed class SwaggerEnvironmentFacts : IClassFixture<Phase7WebAppFactory>
{
    private readonly Phase7WebAppFactory _devFactory;

    public SwaggerEnvironmentFacts(Phase7WebAppFactory devFactory) => _devFactory = devFactory;

    [Fact]
    public async Task SwaggerDocV1_Returns_200_In_Development()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _devFactory.CreateClient();  // default env is Development

        var response = await client.GetAsync("/swagger/v1/swagger.json", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerUi_Returns_200_In_Development()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _devFactory.CreateClient();

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
        // Kept per-fact: ProductionWebAppFactory is internal sealed, which is incompatible with
        // IClassFixture<T>'s public-type requirement (IN-05 only hoists the public Phase7WebAppFactory).
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SwaggerDocV1_Returns_404_In_Production()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new ProductionWebAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
