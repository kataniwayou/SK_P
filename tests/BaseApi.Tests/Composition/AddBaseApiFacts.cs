using BaseApi.Core.DependencyInjection;
using BaseApi.Core.Health;
using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Repositories;
using BaseApi.Service;                       // AppDbContext
using BaseApi.Tests.Validation;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>HTTP-13 — AddBaseApi&lt;TDbContext&gt;(cfg) registers every base concern; each
/// type below resolves from a scope without throwing.</summary>
public sealed class AddBaseApiFacts
{
    // IN-04: build the test connection string from NpgsqlConnectionStringBuilder + env vars
    // (with the compose.yaml dev defaults as fallback) so credentials are not literal strings
    // in source control. No test in this file actually opens a DB connection; the string is
    // exercised only by DI-graph shape assertions.
    private static string TestConnectionString { get; } = new NpgsqlConnectionStringBuilder
    {
        Host     = "localhost",
        Port     = 5433,
        Database = "stepsdb_addbaseapi",
        Username = Environment.GetEnvironmentVariable("POSTGRES_USER")     ?? "postgres",
        Password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres",
    }.ConnectionString;

    private static IServiceCollection BuildServices()
    {
        // IN-03: skip the throwaway BuildServiceProvider() round-trip — the IConfiguration
        // instance we just constructed IS the one DI would have handed back.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = TestConnectionString,
                ["Service:Name"]               = "sk-api-test",
                ["Service:Version"]            = "0.0.0-test",
            }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(cfg);

        // Adding to a fresh assembly scan; AppDbContext.Assembly is BaseApi.Service.dll.
        services.AddBaseApi<AppDbContext>(cfg);
        // Also scan the Tests assembly so TestDtoValidator + TestCreateDtoValidator + TestEntityMapper are visible.
        services.AddBaseApiValidation(typeof(AddBaseApiFacts).Assembly);
        services.AddBaseApiMapping(typeof(AddBaseApiFacts).Assembly);
        return services;
    }

    [Fact]
    public void AddBaseApi_Registers_AppDbContext()
    {
        using var provider = BuildServices().BuildServiceProvider();
        using var scope    = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<AppDbContext>());
    }

    [Fact]
    public void AddBaseApi_Aliases_BaseDbContext_To_AppDbContext_Scoped()
    {
        using var provider = BuildServices().BuildServiceProvider();
        using var scope    = provider.CreateScope();
        var concrete = scope.ServiceProvider.GetService<AppDbContext>();
        var alias    = scope.ServiceProvider.GetService<BaseDbContext>();
        Assert.NotNull(concrete); Assert.NotNull(alias);
        Assert.Same(concrete, alias);   // alias resolves to the same instance — Scoped lifetime
    }

    [Fact]
    public void AddBaseApi_Registers_Repository_Open_Generic()
    {
        using var provider = BuildServices().BuildServiceProvider();
        using var scope    = provider.CreateScope();
        var repo = scope.ServiceProvider.GetService<IRepository<TestEntity>>();
        Assert.NotNull(repo);
    }

    [Fact]
    public void AddBaseApi_Registers_IStartupGate_Singleton()
    {
        using var provider = BuildServices().BuildServiceProvider();
        var gate1 = provider.GetService<IStartupGate>();
        var gate2 = provider.GetService<IStartupGate>();
        Assert.NotNull(gate1);
        Assert.Same(gate1, gate2);
    }

    [Fact]
    public void AddBaseApi_Registers_TestDtoValidator_Via_Phase6_Scan()
    {
        // Blocker 2 fix: BOTH IValidator<TestCreateDto> AND IValidator<TestUpdateDto> must
        // resolve. The TestCreateDtoValidator added in Task 1 satisfies the Create side;
        // the existing TestDtoValidator (covers TestUpdateDto) satisfies the Update side.
        using var provider = BuildServices().BuildServiceProvider();
        using var scope    = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<IValidator<TestCreateDto>>());
        Assert.NotNull(scope.ServiceProvider.GetService<IValidator<TestUpdateDto>>());
    }

    [Fact]
    public void AddBaseApi_Registers_TestEntityMapper_Via_Phase6_Scan()
    {
        using var provider = BuildServices().BuildServiceProvider();
        using var scope    = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider
            .GetService<IEntityMapper<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>>());
    }
}
