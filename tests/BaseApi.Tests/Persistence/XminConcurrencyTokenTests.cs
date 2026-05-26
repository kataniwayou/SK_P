using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class XminConcurrencyTokenTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public XminConcurrencyTokenTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public void Test_BaseEntity_HasXminShadowProperty()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        using var db = new TestDbContext(options);

        var entityType = db.Model.FindEntityType(typeof(TestEntity));
        Assert.NotNull(entityType);

        var xmin = entityType!.FindProperty("xmin");
        Assert.NotNull(xmin);
        Assert.True(xmin!.IsConcurrencyToken);
        Assert.Equal("xid", xmin.GetColumnType());
    }
}
