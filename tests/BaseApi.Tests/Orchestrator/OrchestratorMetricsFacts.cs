using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Observability;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Hermetic guard for the <see cref="OrchestratorMetrics"/> holder (METRIC-04). Constructs it from a
/// real <see cref="IMeterFactory"/> (the .NET 8 blessed pattern — no <c>static Meter</c> field, so no
/// cross-test static leak in the shared hermetic process) and asserts the two counters are non-null
/// AND the meter-name const equals "Orchestrator". This catches the D-02 meter-name/const-mismatch bug
/// (the const must equal the <c>AddMeter("Orchestrator")</c> string) WITHOUT the full stack.
/// </summary>
public sealed class OrchestratorMetricsFacts
{
    [Fact]
    public void MeterName_Is_Orchestrator()
    {
        Assert.Equal("Orchestrator", OrchestratorMetrics.MeterName);
    }

    [Fact]
    public void Constructs_With_NonNull_Counters_From_Real_MeterFactory()
    {
        using var provider = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();

        var metrics = new OrchestratorMetrics(meterFactory);

        Assert.NotNull(metrics.DispatchSent);
        Assert.NotNull(metrics.ResultConsumed);
    }
}
