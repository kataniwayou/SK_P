using System.Text.RegularExpressions;
using BaseApi.Core.DependencyInjection;
using BaseApi.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>
/// Phase 12 INFRA-COMP-01..03 + OBSERV-REDIS-01 — verifies the AddBaseApi DI chain
/// for the Redis-side concerns: Singleton IConnectionMultiplexer (D-14 / Pitfall 1
/// mitigation), IDatabase NOT registered (INFRA-COMP-03), AddBaseApiRedis chained
/// as call #7 after AddBaseApiMapping (INFRA-COMP-01), and the solution-wide
/// negative-grep that no csproj/props references the forbidden OTel Redis
/// instrumentation package (Phase 11 D-03 + OBSERV-REDIS-01).
/// </summary>
[Trait("Phase12Wave", "C")]
public sealed class BaseApiCompositionFacts
{
    private const string TestPostgresConnectionString =
        "Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres";
    private const string TestRedisConnectionString =
        "localhost:6380,abortConnect=false,connectTimeout=5000";

    private static IServiceCollection BuildServices()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = TestPostgresConnectionString,
                ["ConnectionStrings:Redis"]   = TestRedisConnectionString,
                ["Service:Name"]              = "sk-api-test",
                ["Service:Version"]           = "0.0.0-test",
            }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddBaseApi<AppDbContext>(cfg);
        services.AddBaseApiValidation(typeof(BaseApiCompositionFacts).Assembly);
        services.AddBaseApiMapping(typeof(BaseApiCompositionFacts).Assembly);
        return services;
    }

    [Fact]
    public void AddBaseApi_Registers_IConnectionMultiplexer_Singleton()
    {
        var services = BuildServices();
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IConnectionMultiplexer));
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Singleton, descriptor!.Lifetime);
    }

    [Fact]
    public void AddBaseApi_Singleton_Multiplexer_Is_ReferenceEqual_Across_Scopes()
    {
        using var provider = BuildServices().BuildServiceProvider();
        var rootMux = provider.GetRequiredService<IConnectionMultiplexer>();
        using (var scope = provider.CreateScope())
        {
            var scopedMux = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            Assert.Same(rootMux, scopedMux);
        }
    }

    [Fact]
    public void AddBaseApi_Does_NOT_Register_IDatabase()
    {
        var services = BuildServices();
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IDatabase));
        Assert.Null(descriptor);
    }

    [Fact]
    public void AddBaseApi_Chains_AddBaseApiRedis_After_AddBaseApiMapping()
    {
        var path = Path.Combine(FindRepoRoot(),
            "src", "BaseApi.Core", "DependencyInjection",
            "BaseApiServiceCollectionExtensions.cs");
        Assert.True(File.Exists(path), $"Composition root file missing: {path}");
        var content = File.ReadAllText(path);
        // Literal-aware regex: matches the actual chain text
        //   .AddBaseApiMapping(typeof(TDbContext).Assembly)
        //   .AddBaseApiRedis(cfg)
        // The nested parens in `typeof(TDbContext).Assembly` mean `[^)]+` would NOT match
        // (closes after the inner paren); we pin the literal call shape instead, and allow
        // an optional inline comment + whitespace before the next chain call.
        Assert.Matches(
            new Regex(@"\.AddBaseApiMapping\(typeof\(TDbContext\)\.Assembly\)\s*(//[^\r\n]*)?\s*\r?\n\s*\.AddBaseApiRedis\(cfg\)",
                      RegexOptions.Multiline),
            content);
    }

    [Fact]
    public void Solution_Csproj_Does_NOT_Reference_OpenTelemetry_StackExchangeRedis()
    {
        // OBSERV-REDIS-01 + Phase 11 D-03 invariant — solution-wide CI enforcement.
        var root = FindRepoRoot();
        var hits = new List<string>();
        foreach (var pattern in new[] { "*.csproj", "*.props" })
        {
            foreach (var file in Directory.GetFiles(root, pattern, SearchOption.AllDirectories))
            {
                // Skip /bin/ + /obj/ derived files which can transitively reference packages.
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;
                var text = File.ReadAllText(file);
                if (text.Contains("OpenTelemetry.Instrumentation.StackExchangeRedis"))
                    hits.Add(file);
            }
        }
        Assert.Empty(hits);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SK_P.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate SK_P.sln walking up from " + AppContext.BaseDirectory);
    }
}
