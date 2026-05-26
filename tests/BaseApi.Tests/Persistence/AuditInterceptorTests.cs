using BaseApi.Core.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class AuditInterceptorTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public AuditInterceptorTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Test_AuditInterceptor_StampsUtcTimestamps_OnInsert()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero));

        var stub = new StubHttpContextAccessor();
        stub.SetUser("alice");

        var interceptor = new AuditInterceptor(stub, clock);

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new TestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var entity = new TestEntity();
        await db.TestEntities.AddAsync(entity);
        await db.SaveChangesAsync();

        Assert.Equal(new DateTime(2026, 1, 15, 12, 30, 0, DateTimeKind.Utc), entity.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, entity.CreatedAt.Kind);
        Assert.Equal("alice", entity.CreatedBy);
    }

    [Fact]
    public async Task Test_AuditInterceptor_StampsCreatedBy_FromHttpContext_NullFallback()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero));

        var stub = new StubHttpContextAccessor(); // HttpContext == null
        var interceptor = new AuditInterceptor(stub, clock);

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new TestDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var entity = new TestEntity();
        await db.TestEntities.AddAsync(entity);
        await db.SaveChangesAsync();  // must NOT throw

        Assert.Null(entity.CreatedBy);
    }
}
