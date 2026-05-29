using BaseApi.Core.Configuration;
using BaseApi.Core.DependencyInjection;
using BaseApi.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>
/// Phase 12 INFRA-REDIS-05 + INFRA-COMP-04 — verifies RedisProjectionOptions binding
/// from cfg.GetSection("Redis") through services.Configure&lt;&gt; into IOptions
/// resolution. Defaults (D-15: KeyPrefix "skp:" + Serialization.JsonOptions "default")
/// and override flow.
/// </summary>
[Trait("Phase12Wave", "C")]
public sealed class RedisProjectionOptionsBindingFacts
{
    private const string TestPostgresConnectionString =
        "Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres";
    private const string TestRedisConnectionString =
        "localhost:6380,abortConnect=false,connectTimeout=5000";

    private static ServiceProvider BuildProvider(IDictionary<string, string?>? extra = null)
    {
        var baseCfg = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = TestPostgresConnectionString,
            ["ConnectionStrings:Redis"]   = TestRedisConnectionString,
            ["Service:Name"]              = "sk-api-test",
            ["Service:Version"]           = "0.0.0-test",
        };
        if (extra is not null) foreach (var kv in extra) baseCfg[kv.Key] = kv.Value;

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(baseCfg).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddBaseApi<AppDbContext>(cfg);
        services.AddBaseApiValidation(typeof(RedisProjectionOptionsBindingFacts).Assembly);
        services.AddBaseApiMapping(typeof(RedisProjectionOptionsBindingFacts).Assembly);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Defaults_Bind_KeyPrefix_skp()
    {
        using var provider = BuildProvider();
        var opts = provider.GetRequiredService<IOptions<RedisProjectionOptions>>();
        Assert.Equal("skp:", opts.Value.KeyPrefix);
    }

    [Fact]
    public void Defaults_Bind_Serialization_JsonOptions_default()
    {
        using var provider = BuildProvider();
        var opts = provider.GetRequiredService<IOptions<RedisProjectionOptions>>();
        Assert.Equal("default", opts.Value.Serialization.JsonOptions);
    }

    [Fact]
    public void InjectedOverride_Reflects_In_IOptions_KeyPrefix()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Redis:KeyPrefix"] = "test:override:",
        });
        var opts = provider.GetRequiredService<IOptions<RedisProjectionOptions>>();
        Assert.Equal("test:override:", opts.Value.KeyPrefix);
    }

    [Fact]
    public void InjectedOverride_Reflects_In_IOptions_Serialization_JsonOptions()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Redis:Serialization:JsonOptions"] = "snake_case",
        });
        var opts = provider.GetRequiredService<IOptions<RedisProjectionOptions>>();
        Assert.Equal("snake_case", opts.Value.Serialization.JsonOptions);
    }
}
