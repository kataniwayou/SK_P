using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using BaseProcessor.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Observability;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 32 Wave-1 (req-7, D-10/D-11). Hermetic guard for the THREE new breaker/dedup counters added to
/// the existing Phase-30 meters: the processor-side <c>processor_dispatch_deduped</c> +
/// <c>workflow_cancelled</c> (the trip is processor-side per D-01) and the orchestrator-side
/// <c>orchestrator_result_deduped</c>. Constructs both holders from a real <see cref="IMeterFactory"/>
/// (the .NET 8 blessed pattern — no <c>static Meter</c> field, so no cross-test static leak) and asserts:
/// <list type="bullet">
///   <item><description>all three new counters are non-null;</description></item>
///   <item><description>the meter-name consts are unchanged (<c>"BaseProcessor"</c> / <c>"Orchestrator"</c>);</description></item>
///   <item><description>a recorded measurement carries the bounded <c>ProcessorId</c> tag but NO
///   <c>workflowId</c>/<c>WorkflowId</c> tag key (T-32-02 cardinality guard, mirrors T-30-04).</description></item>
/// </list>
/// Plan 04 extends THIS class with the increment-once-per-trip / once-per-drop behavioral assertions at
/// the real trip/drop sites. Hermetic (default Category) — no real stack.
/// </summary>
public sealed class BreakerMetricsFacts
{
    private static IMeterFactory NewMeterFactory(out ServiceProvider provider)
    {
        provider = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider();
        return provider.GetRequiredService<IMeterFactory>();
    }

    [Fact]
    public void Meter_Name_Consts_Are_Unchanged()
    {
        Assert.Equal("BaseProcessor", ProcessorMetrics.MeterName);
        Assert.Equal("Orchestrator", OrchestratorMetrics.MeterName);
    }

    [Fact]
    public void Processor_New_Counters_Construct_NonNull()
    {
        var meterFactory = NewMeterFactory(out var provider);
        using (provider)
        {
            var metrics = new ProcessorMetrics(meterFactory);

            Assert.NotNull(metrics.DispatchDeduped);
            Assert.NotNull(metrics.WorkflowCancelled);
        }
    }

    [Fact]
    public void Orchestrator_New_Counter_Constructs_NonNull()
    {
        var meterFactory = NewMeterFactory(out var provider);
        using (provider)
        {
            var metrics = new OrchestratorMetrics(meterFactory);

            Assert.NotNull(metrics.ResultDeduped);
        }
    }

    [Fact]
    public void Recorded_Measurement_Carries_ProcessorId_But_No_WorkflowId_Label()
    {
        // Drive each new counter with the same ProcessorId tag the real increment sites use
        // (value .ToString("D")), then verify via a MeterListener that the measurement carries a
        // ProcessorId tag and NO workflowId/WorkflowId tag key (the cardinality guard, T-32-04).
        var processorId = new System.Guid("99999999-9999-9999-9999-999999999999").ToString("D");

        var capturedTagKeySets = new List<string[]>();

        var procFactory = NewMeterFactory(out var procProvider);
        var orchFactory = NewMeterFactory(out var orchProvider);
        using (procProvider)
        using (orchProvider)
        {
            var procMetrics = new ProcessorMetrics(procFactory);
            var orchMetrics = new OrchestratorMetrics(orchFactory);

            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name is ProcessorMetrics.MeterName or OrchestratorMetrics.MeterName)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            {
                capturedTagKeySets.Add(tags.ToArray().Select(t => t.Key).ToArray());
            });
            listener.Start();

            var tag = new KeyValuePair<string, object?>("ProcessorId", processorId);
            procMetrics.DispatchDeduped.Add(1, tag);
            procMetrics.WorkflowCancelled.Add(1, tag);
            orchMetrics.ResultDeduped.Add(1, tag);
        }

        Assert.Equal(3, capturedTagKeySets.Count);
        foreach (var keys in capturedTagKeySets)
        {
            Assert.Contains("ProcessorId", keys);
            Assert.DoesNotContain("workflowId", keys);
            Assert.DoesNotContain("WorkflowId", keys);
        }
    }
}
