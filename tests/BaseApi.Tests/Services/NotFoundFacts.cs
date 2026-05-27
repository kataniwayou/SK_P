using System.Net;
using System.Text.Json;
using BaseApi.Tests.Composition;
using Xunit;

namespace BaseApi.Tests.Services;

/// <summary>HTTP-09 + ERROR-06 regression — BaseService.GetByIdAsync throws NotFoundException
/// when the id is missing; Phase 4 NotFoundExceptionHandler maps to 404 ProblemDetails with
/// correlationId + resourceType + resourceId.</summary>
public sealed class NotFoundFacts
{
    [Fact]
    public async Task Get_NonexistentTest_Returns_404_ProblemDetails_With_ResourceType_TestEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new Phase7WebAppFactory();
        await factory.InitializeAsync();
        using var client  = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/tests/{Guid.NewGuid()}", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("TestEntity", doc.RootElement.GetProperty("resourceType").GetString());
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));

        // The correlationId field MUST equal the X-Correlation-Id response header.
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var values));
        var headerId = values!.First();
        Assert.Equal(headerId, doc.RootElement.GetProperty("correlationId").GetString());
    }
}
