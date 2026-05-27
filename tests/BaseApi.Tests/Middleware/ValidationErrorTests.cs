using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BaseApi.Tests.Middleware;

/// <summary>
/// SC#2 (ERROR-03) — FluentValidation 400 path produces ProblemDetails
/// + field-level errors map + correlationId matching X-Correlation-Id response header.
/// SC#5 (ERROR-10) — [ApiController] model-binding 400 produces the SAME ProblemDetails
/// shape with correlationId + instance extensions (D-11 single-source-of-truth).
/// </summary>
public sealed class ValidationErrorTests
{
    [Fact]
    public async Task Test_FluentValidation_Exception_Produces_400_WithErrorsMap_AndCorrelationId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/test/validation-error-via-fv", null, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("errors", out var errors));
        Assert.True(errors.TryGetProperty("Version", out var versionErrors));
        Assert.Contains(versionErrors.EnumerateArray(), e => e.GetString()!.Contains("SemVer"));

        Assert.True(doc.RootElement.TryGetProperty("correlationId", out var corrProp));
        var corr = corrProp.GetString()!;

        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var hdr));
        Assert.Equal(hdr!.First(), corr);
    }

    [Fact]
    public async Task Test_ModelBinding_400_ProducesSameShape_AsFluentValidation400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        // Missing required Name field — [ApiController] auto-rejects with 400 ProblemDetails.
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/test/validation-error-via-modelbinding", content, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("errors", out _));
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out var corrProp));
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var hdr));
        Assert.Equal(hdr!.First(), corrProp.GetString());
    }
}
