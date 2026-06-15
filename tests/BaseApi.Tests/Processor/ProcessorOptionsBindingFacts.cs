using BaseProcessor.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// CONFIG-01 / CONFIG-02 / D-11/D-12 (Phase 60): <see cref="ProcessorLivenessOptions"/> binds six
/// INDEPENDENT seconds knobs from the <c>"Processor"</c> config section —
/// <c>Interval</c>/<c>StartupInterval</c>/<c>Ttl</c>/<c>RequestTimeout</c>/<c>BackoffCap</c>/<c>ExecutionDataTtl</c>.
/// The property names carry the <c>Seconds</c> suffix; <c>[ConfigurationKeyName]</c> maps each to
/// the bare config key. An empty config yields the baked defaults.
/// </summary>
[Trait("Phase", "60")]
public sealed class ProcessorOptionsBindingFacts
{
    [Fact]
    public void Binds_Six_Independent_Seconds_Knobs_From_Processor_Section()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Processor:Interval"]         = "10",
                ["Processor:StartupInterval"]  = "30",
                ["Processor:Ttl"]              = "45",
                ["Processor:RequestTimeout"]   = "7",
                ["Processor:BackoffCap"]       = "30",
                ["Processor:ExecutionDataTtl"] = "120",
            })
            .Build();

        var opts = cfg.GetSection("Processor").Get<ProcessorLivenessOptions>();

        Assert.NotNull(opts);
        // Interval and Ttl bind to two INDEPENDENT properties (CONFIG-01).
        Assert.Equal(10, opts!.IntervalSeconds);
        // StartupInterval binds to its own INDEPENDENT property (D-11/D-12, Phase 60).
        Assert.Equal(30, opts.StartupIntervalSeconds);
        Assert.Equal(45, opts.TtlSeconds);
        Assert.Equal(7, opts.RequestTimeoutSeconds);
        Assert.Equal(30, opts.BackoffCapSeconds);
        // ExecutionDataTtl binds to its own INDEPENDENT property (CONFIG-02 / D-17).
        Assert.Equal(120, opts.ExecutionDataTtlSeconds);
    }

    [Fact]
    public void Empty_Config_Yields_Baked_Defaults()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var opts = cfg.GetSection("Processor").Get<ProcessorLivenessOptions>() ?? new ProcessorLivenessOptions();

        Assert.Equal(10, opts.IntervalSeconds);
        Assert.Equal(30, opts.StartupIntervalSeconds); // D-12: baked default 30 = BackoffCap anchor
        Assert.Equal(30, opts.TtlSeconds);
        Assert.Equal(8, opts.RequestTimeoutSeconds);
        Assert.Equal(30, opts.BackoffCapSeconds);
        Assert.Equal(300, opts.ExecutionDataTtlSeconds);
    }
}
