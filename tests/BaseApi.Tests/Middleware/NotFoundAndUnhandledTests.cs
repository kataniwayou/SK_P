using System.Net;
using System.Text.Json;
using Xunit;

namespace BaseApi.Tests.Middleware;

/// <summary>
/// SC#4 (ERROR-01/02/06/07 + T-04-LEAK) — NotFoundException produces 404 with
/// resource type + id in detail AND Extensions; unhandled exception produces
/// 500 with a generic message AND the response body contains NO stack-frame
/// text or internal exception type / message text (information-disclosure guard).
/// </summary>
public sealed class NotFoundAndUnhandledTests
{
    [Fact]
    public async Task Test_NotFoundException_Produces_404_With_ResourceType_And_Id()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/test/not-found", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());

        var detail = doc.RootElement.GetProperty("detail").GetString()!;
        Assert.Contains("Schema", detail);
        Assert.Contains("was not found", detail);

        Assert.Equal("Schema", doc.RootElement.GetProperty("resourceType").GetString());
        Assert.True(doc.RootElement.TryGetProperty("resourceId", out _));
    }

    [Fact]
    public async Task Test_Unhandled_Exception_Produces_500_With_NoStackTraceInBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/test/unhandled", ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(500, doc.RootElement.GetProperty("status").GetInt32());

        // FallbackExceptionHandler shape (D-12).
        var detail = doc.RootElement.GetProperty("detail").GetString()!;
        Assert.Contains("unexpected", detail, StringComparison.OrdinalIgnoreCase);

        // T-04-LEAK: information-disclosure guard.
        // The body must contain NEITHER the exception type, NOR its message, NOR a stack frame.
        Assert.DoesNotContain("at BaseApi", body);
        Assert.DoesNotContain("InvalidOperation", body);
        Assert.DoesNotContain("This message should NOT leak", body);
        Assert.DoesNotContain(".cs:line", body);
        Assert.DoesNotContain("StackTrace", body);
    }
}
