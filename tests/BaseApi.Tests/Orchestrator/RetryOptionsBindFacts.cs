using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Hermetic guard for the per-process <see cref="RetryOptions"/> bind (req-7, D-10). Proves the
/// retry budget is a single config-bound source of truth: a "Retry" section binds the
/// <see cref="RetryOptions.Limit"/> the 4 <c>UseMessageRetry(Immediate(Limit))</c> sites read, an
/// absent section defaults to <c>Immediate(3)</c>, and <see cref="RetryStrategy"/> binds by name. The
/// live attempt-count proof (changing Limit changes the effective retry budget against the real broker)
/// is deferred to the req-8 E2E (Plan 06); binding correctness is the unit-tier sample (VALIDATION § Tier 1).
/// </summary>
public sealed class RetryOptionsBindFacts
{
    private static RetryOptions Bind(params KeyValuePair<string, string?>[] entries)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(entries)
            .Build();

        using var provider = new ServiceCollection()
            .Configure<RetryOptions>(config.GetSection("Retry"))
            .BuildServiceProvider();

        return provider.GetRequiredService<IOptions<RetryOptions>>().Value;
    }

    [Fact]
    public void Binds_Limit_From_Section()
    {
        var opts = Bind(
            new KeyValuePair<string, string?>("Retry:Limit", "7"),
            new KeyValuePair<string, string?>("Retry:Strategy", "Immediate"));

        Assert.Equal(7, opts.Limit);
        Assert.Equal(RetryStrategy.Immediate, opts.Strategy);
    }

    [Fact]
    public void Defaults_To_Immediate3_When_Absent()
    {
        // EMPTY configuration — no "Retry" section at all → the baked RetryOptions defaults stand.
        var opts = Bind();

        Assert.Equal(3, opts.Limit);
        Assert.Equal(RetryStrategy.Immediate, opts.Strategy);
    }

    [Fact]
    public void Strategy_Binds_Enum_ByName()
    {
        // Proves the enum binds by name even though only the Immediate branch is wired this phase
        // (Interval/Exponential are structured-for, not honored — D-10).
        var opts = Bind(
            new KeyValuePair<string, string?>("Retry:Strategy", "Exponential"));

        Assert.Equal(RetryStrategy.Exponential, opts.Strategy);
    }
}
