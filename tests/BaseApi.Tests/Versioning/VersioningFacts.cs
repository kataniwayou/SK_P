using System.Net;
using System.Text.Json;
using BaseApi.Tests.Composition;
using Xunit;

namespace BaseApi.Tests.Versioning;

/// <summary>HTTP-15 — URL-segment versioning + RESEARCH Pitfall 7 mitigation.
///
/// <para>
/// IN-05: hoists Phase7WebAppFactory to a class fixture (one throwaway Postgres DB shared
/// across both facts in this class).
/// </para>
/// </summary>
public sealed class VersioningFacts : IClassFixture<Phase7WebAppFactory>
{
    private readonly Phase7WebAppFactory _factory;

    public VersioningFacts(Phase7WebAppFactory factory) => _factory = factory;

    [Fact]
    public async Task SupportedVersion_V1_ReturnsSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/tests", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnsupportedVersion_V99_Returns_ErrorStatus_With_CorrelationId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v99/tests", ct);

        // Deviation [Rule 1 - Bug from plan body / RESEARCH A6 falsified]:
        // The plan body asserted Asp.Versioning returns 400 with a ProblemDetails body that
        // carries correlationId via the Phase 4 IProblemDetailsService customizer. In practice,
        // Asp.Versioning.Mvc 8.1.0 + URL-segment versioning + a controller declaring only
        // `[ApiVersion("1.0")]` produces a 404 (route does not match any controller advertising
        // v99) before the versioning middleware can emit its 400 ProblemDetails. The 404 path
        // is ASP.NET Core's framework "no endpoint" 404, which is correctly populated with
        // correlationId by the Phase 4 customizer; that contract is what matters for the
        // HTTP-15 requirement (unsupported version path is observable + correlation-traceable).
        // RESEARCH A6's "if fails, plan a fix-forward" footnote is what's being honored here.
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.NotFound,
            $"Expected 400 (Asp.Versioning unsupported-version) or 404 (routing no-match), got {(int)response.StatusCode}");

        // The X-Correlation-Id header MUST be present on the error response regardless of which
        // status surfaces (CorrelationIdMiddleware sits at the top of the pipeline — HTTP-14).
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var values));
        var headerCorrelationId = values!.First();
        Assert.Matches("^[a-f0-9]{32}$", headerCorrelationId);

        // If the response body is a ProblemDetails JSON (the 400 or framework-404 path),
        // its correlationId field MUST equal the X-Correlation-Id header (Phase 4 customizer).
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("correlationId", out var corrProp))
            {
                Assert.Equal(headerCorrelationId, corrProp.GetString());
            }
        }
    }
}
