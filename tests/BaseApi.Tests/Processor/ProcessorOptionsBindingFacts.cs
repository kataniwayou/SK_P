using BaseProcessor.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// CONFIG-01 / CONFIG-02: <see cref="ProcessorLivenessOptions"/> binds five INDEPENDENT seconds knobs
/// from the <c>"Processor"</c> config section —
/// <c>Interval</c>/<c>Ttl</c>/<c>RequestTimeout</c>/<c>BackoffCap</c>/<c>ExecutionDataTtl</c>.
/// The property names carry the <c>Seconds</c> suffix; <c>[ConfigurationKeyName]</c> maps each to
/// the bare config key. An empty config yields the baked defaults.
/// </summary>
public sealed class ProcessorOptionsBindingFacts
{
    [Fact]
    public void Binds_Five_Independent_Seconds_Knobs_From_Processor_Section()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Processor:Interval"]         = "10",
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
        Assert.Equal(30, opts.TtlSeconds);
        Assert.Equal(8, opts.RequestTimeoutSeconds);
        Assert.Equal(30, opts.BackoffCapSeconds);
        Assert.Equal(300, opts.ExecutionDataTtlSeconds);
    }

    [Fact]
    public void SlotArray_Binds_Min_Max_From_Processor_Section()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Processor:SlotArrayTtlMin"] = "300",
                ["Processor:SlotArrayTtlMax"] = "600",
            })
            .Build();

        var opts = cfg.GetSection("Processor").Get<SlotArrayOptions>();

        Assert.NotNull(opts);
        Assert.Equal(300, opts!.SlotArrayTtlMinSeconds);
        Assert.Equal(600, opts.SlotArrayTtlMaxSeconds);
    }

    [Fact]
    public void SlotArray_Empty_Config_Yields_Baked_Defaults()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var opts = cfg.GetSection("Processor").Get<SlotArrayOptions>() ?? new SlotArrayOptions();

        Assert.Equal(300, opts.SlotArrayTtlMinSeconds);
        Assert.Equal(600, opts.SlotArrayTtlMaxSeconds);
    }
}
