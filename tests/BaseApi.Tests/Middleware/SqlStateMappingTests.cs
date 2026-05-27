using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseApi.Tests.Middleware;

/// <summary>
/// SC#3 (ERROR-04 + ERROR-05 + ERROR-11) — real Postgres FK + UQ violations
/// surface through the EF Core layer + DbUpdateExceptionHandler +
/// PostgresExceptionMapper (Option A regex) to RFC 7807 responses with the
/// offending column name in detail and correlationId + instance extensions.
/// </summary>
public sealed class SqlStateMappingTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public SqlStateMappingTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Test_FK_Violation_23503_Maps_To_422_WithColumnInDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/test/fk-violation", null, ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(422, doc.RootElement.GetProperty("status").GetInt32());
        var detail = doc.RootElement.GetProperty("detail").GetString()!;
        Assert.Contains("parent_id", detail);
        // T-04-LEAK: no internal Postgres text / stack frames in body.
        Assert.DoesNotContain("at BaseApi", body);
        Assert.DoesNotContain("Npgsql.PostgresException", body);
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));
        Assert.True(doc.RootElement.TryGetProperty("instance", out _));
    }

    [Fact]
    public async Task Test_UQ_Violation_23505_Maps_To_409_WithColumnInDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        // Seed parent + first child out-of-band so the second insert via the endpoint
        // triggers 23505 (not 23503) — using the same parentId for both.
        var options = new DbContextOptionsBuilder<TestErrorDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        var parentId = Guid.NewGuid();
        var dupName = $"dup-{Guid.NewGuid():N}";
        await using (var db = new TestErrorDbContext(options))
        {
            await db.Database.EnsureCreatedAsync(ct);
            db.Parents.Add(new BaseApi.Tests.Middleware.TestParentEntity
            {
                Id = parentId,
                Name = "p",
                Version = "1.0.0",
            });
            db.Children.Add(new BaseApi.Tests.Middleware.TestChildEntity
            {
                Name = dupName,
                Version = "1.0.0",
                ParentId = parentId,
            });
            await db.SaveChangesAsync(ct);
        }

        // Now hit the HTTP endpoint with the same name + parentId — endpoint duplicates → 23505 → 409.
        var response = await client.PostAsync(
            $"/test/unique-violation?name={dupName}&parentId={parentId}", null, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(409, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Contains("name", doc.RootElement.GetProperty("detail").GetString()!);
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));
        Assert.True(doc.RootElement.TryGetProperty("instance", out _));
    }
}
