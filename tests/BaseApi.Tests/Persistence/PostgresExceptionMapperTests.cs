using BaseApi.Core.Persistence.Exceptions;
using BaseApi.Tests.Middleware;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BaseApi.Tests.Persistence;

/// <summary>
/// ERROR-11 unit coverage — verifies <see cref="PostgresExceptionMapper.TryMap"/>
/// against real Postgres FK + UQ violations (Option A regex extraction preserving
/// <c>_id</c> suffix) plus the null-inner and non-Postgres-inner fallthrough
/// branches. Uses the lifted <see cref="PostgresFixture"/> from the
/// <c>BaseApi.Tests.Middleware</c> namespace (per Plan 04-02 PATTERNS.md and
/// RESEARCH.md Open Question 1 — real-DB exception capture is the cleanest
/// approach because Npgsql.PostgresException has no public test-friendly
/// constructor).
/// </summary>
public sealed class PostgresExceptionMapperTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresExceptionMapperTests(PostgresFixture fixture) => _fixture = fixture;

    private DbContextOptions<TestErrorDbContext> Options() =>
        new DbContextOptionsBuilder<TestErrorDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

    [Fact]
    public async Task Test_FK_Violation_TryMap_Returns_422_And_Extracts_Column_PreservingIdSuffix()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new TestErrorDbContext(Options());
        await db.Database.EnsureCreatedAsync(ct);

        db.Children.Add(new TestChildEntity
        {
            Name = $"child-{Guid.NewGuid():N}",
            Version = "1.0.0",
            ParentId = Guid.NewGuid(),  // non-existent
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(async () => await db.SaveChangesAsync(ct));
        var ok = PostgresExceptionMapper.TryMap(ex, out var status, out var detail, out var col);

        Assert.True(ok);
        Assert.Equal(422, status);
        Assert.Equal("parent_id", col);   // Option A regex preserves _id suffix
        Assert.Contains("parent_id", detail);
    }

    [Fact]
    public async Task Test_UQ_Violation_TryMap_Returns_409_And_Extracts_Column()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = new TestErrorDbContext(Options());
        await db.Database.EnsureCreatedAsync(ct);

        var parentId = Guid.NewGuid();
        db.Parents.Add(new TestParentEntity { Id = parentId, Name = "p", Version = "1.0.0" });
        var dupName = $"dup-{Guid.NewGuid():N}";
        db.Children.Add(new TestChildEntity { Name = dupName, Version = "1.0.0", ParentId = parentId });
        await db.SaveChangesAsync(ct);

        db.Children.Add(new TestChildEntity { Name = dupName, Version = "1.0.0", ParentId = parentId });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(async () => await db.SaveChangesAsync(ct));
        var ok = PostgresExceptionMapper.TryMap(ex, out var status, out var detail, out var col);

        Assert.True(ok);
        Assert.Equal(409, status);
        Assert.Equal("name", col);
        Assert.Contains("name", detail);
    }

    [Fact]
    public void Test_Null_Inner_Returns_False()
    {
        var ex = new DbUpdateException("no inner");  // InnerException is null
        var ok = PostgresExceptionMapper.TryMap(ex, out var status, out var detail, out var col);
        Assert.False(ok);
        Assert.Null(col);
    }

    [Fact]
    public void Test_NonPostgres_Inner_Returns_False()
    {
        var ex = new DbUpdateException("wrapped", new InvalidOperationException("not PG"));
        var ok = PostgresExceptionMapper.TryMap(ex, out var status, out var detail, out var col);
        Assert.False(ok);
        Assert.Null(col);
    }
}
