using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class DiLifetimeTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public DiLifetimeTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public void Test_DbContext_IsRegisteredScoped_InDI()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IHttpContextAccessor, StubHttpContextAccessor>();
        services.AddDbContext<TestDbContext>(opts => opts.UseNpgsql(_fixture.ConnectionString));

        var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        var a = scope1.ServiceProvider.GetRequiredService<TestDbContext>();
        var b = scope1.ServiceProvider.GetRequiredService<TestDbContext>();
        Assert.Same(a, b);

        using var scope2 = provider.CreateScope();
        var c = scope2.ServiceProvider.GetRequiredService<TestDbContext>();
        Assert.NotSame(a, c);
    }
}
