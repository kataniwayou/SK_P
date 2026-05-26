using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class SchemaTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public SchemaTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Test_TestDbContext_EnsureCreated_ProducesSnakeCaseSchema()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var ct = TestContext.Current.CancellationToken;
        await using var db = new TestDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT column_name FROM information_schema.columns
            WHERE table_name = 'test_entities' ORDER BY column_name";
        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(0));

        Assert.Contains("created_at", columns);
        Assert.Contains("updated_at", columns);
        Assert.Contains("created_by", columns);
        Assert.Contains("updated_by", columns);
        Assert.Contains("id", columns);
        Assert.Contains("name", columns);
        Assert.Contains("version", columns);
        Assert.Contains("description", columns);
        Assert.DoesNotContain("CreatedAt", columns);
        Assert.DoesNotContain("UpdatedAt", columns);
    }
}
