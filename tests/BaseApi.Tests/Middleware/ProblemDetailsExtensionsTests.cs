using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BaseApi.Tests.Middleware;

/// <summary>
/// ERROR-08 + ERROR-09 regression guard — every error response (404, 400, 500,
/// model-binding 400) carries BOTH a <c>correlationId</c> extension AND an
/// <c>instance</c> extension; correlationId matches the X-Correlation-Id
/// response header and instance equals the request path. Verifies the
/// AddProblemDetails CustomizeProblemDetails callback (D-04 single-source-of-truth)
/// runs on every handler-claimed path AND on framework model-binding 400.
/// </summary>
public sealed class ProblemDetailsExtensionsTests
{
    [Theory]
    [InlineData("GET",  "/test/not-found",                   HttpStatusCode.NotFound)]
    [InlineData("GET",  "/test/unhandled",                    HttpStatusCode.InternalServerError)]
    [InlineData("POST", "/test/validation-error-via-fv",      HttpStatusCode.BadRequest)]
    public async Task Test_EveryErrorResponse_Carries_CorrelationId_And_Instance(
        string verb, string path, HttpStatusCode expectedStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        using var req = new HttpRequestMessage(new HttpMethod(verb), path);
        var response = await client.SendAsync(req, ct);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        // ERROR-08: correlationId present + matches X-Correlation-Id header.
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out var corrProp));
        var corr = corrProp.GetString();
        Assert.False(string.IsNullOrEmpty(corr));
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var hdr));
        Assert.Equal(hdr!.First(), corr);

        // ERROR-09: instance equals request path.
        Assert.True(doc.RootElement.TryGetProperty("instance", out var instProp));
        Assert.Equal(path, instProp.GetString());
    }

    [Fact]
    public async Task Test_ModelBinding_400_Also_Carries_CorrelationId_And_Instance()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/test/validation-error-via-modelbinding", content, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("correlationId", out var corr));
        Assert.True(doc.RootElement.TryGetProperty("instance", out var inst));
        Assert.Equal("/test/validation-error-via-modelbinding", inst.GetString());
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var hdr));
        Assert.Equal(hdr!.First(), corr.GetString());
    }
}
