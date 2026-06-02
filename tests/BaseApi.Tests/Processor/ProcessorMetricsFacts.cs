using System.Diagnostics.Metrics;
using BaseProcessor.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Hermetic guard for the <see cref="ProcessorMetrics"/> holder (METRIC-05). Constructs it from a
/// real <see cref="IMeterFactory"/> (the .NET 8 blessed pattern — no <c>static Meter</c> field, so no
/// cross-test static leak in the shared hermetic process) and asserts the two counters are non-null
/// AND the meter-name const equals "BaseProcessor". This catches the D-02 meter-name/const-mismatch bug
/// (the const must equal the <c>AddMeter("BaseProcessor")</c> string) WITHOUT the full stack.
/// </summary>
public sealed class ProcessorMetricsFacts
{
    [Fact]
    public void MeterName_Is_BaseProcessor()
    {
        Assert.Equal("BaseProcessor", ProcessorMetrics.MeterName);
    }

    [Fact]
    public void Constructs_With_NonNull_Counters_From_Real_MeterFactory()
    {
        using var provider = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();

        var metrics = new ProcessorMetrics(meterFactory);

        Assert.NotNull(metrics.DispatchConsumed);
        Assert.NotNull(metrics.ResultSent);
    }
}
