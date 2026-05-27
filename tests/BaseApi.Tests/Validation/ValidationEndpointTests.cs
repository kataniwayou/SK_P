using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BaseApi.Tests.Validation;

/// <summary>
/// SC#3 / VALID-03 — Service-layer <c>IValidator&lt;TestUpdateDto&gt;.ValidateAsync</c>
/// invocation from <see cref="TestValidationService"/> throws
/// <see cref="FluentValidation.ValidationException"/> on bad input, which Phase 4's
/// <c>ValidationExceptionHandler</c> maps to HTTP 400 ProblemDetails with field-level
/// <c>errors</c> map + <c>correlationId</c> matching the <c>X-Correlation-Id</c> response header.
/// </summary>
public sealed class ValidationEndpointTests
{
    [Fact]
    public async Task Test_PostBadDto_Returns400_WithErrorsMap_AndCorrelationId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ValidationWebAppFactory();
        using var client = factory.CreateClient();

        // Bad: Version empty (fails VALID-06).
        var json = """{"name":"ok","version":"","description":null,"note":"n"}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/test/validate", content, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("errors", out var errors));
        Assert.True(errors.TryGetProperty("Version", out var versionErrors));
        Assert.NotEmpty(versionErrors.EnumerateArray());

        Assert.True(doc.RootElement.TryGetProperty("correlationId", out var corrProp));
        var corr = corrProp.GetString()!;

        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var hdr));
        Assert.Equal(hdr!.First(), corr);
    }

    [Fact]
    public async Task Test_PostGoodDto_Returns200_NoProblemDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new ValidationWebAppFactory();
        using var client = factory.CreateClient();

        var json = """{"name":"alice","version":"1.0.0","description":"ok","note":"n"}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/test/validate", content, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
