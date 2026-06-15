using System.Text.Json;                  // JsonSerializer — NOT in .NET 8 implicit usings
using BaseProcessor.Core.Configuration;  // ProcessorConfig.SerializerOptions
using BaseProcessor.Core.Processing;
using Microsoft.Extensions.Logging;

namespace Processor.Sample;

/// <summary>
/// The one concrete <see cref="BaseProcessor{SampleConfig}"/> (PROC-02/PROC-03). It overrides ONLY the
/// typed In-Process <c>ProcessAsync</c> seam; the framework owns deserialization (it hands in a typed
/// <see cref="SampleConfig"/>) and the entire Pre/Post/end-delete pipeline. DELIBERATE multiple-execution
/// stress test: the transform spawns TWO completed <see cref="ProcessItem"/> executions per dispatch, each a
/// {number,label} JSON string with an independent random sum over the config's <c>Number</c> and its own
/// author-minted ExecutionId (D-06), and emits ONE structured log entry per execution tagged with the
/// <c>Step_*</c> label and that execution's sum (D-08). The 6 correlation ids attach automatically from the
/// ambient consume-filter scope (D-09) — none are added here.
/// </summary>
/// <remarks>
/// DI-registered <c>AddSingleton&lt;BaseProcessor, SampleProcessor&gt;</c> (Program.cs:17, unchanged) —
/// SampleProcessor IS-A BaseProcessor via BaseProcessor&lt;SampleConfig&gt;.
/// </remarks>
public sealed class SampleProcessor(ILogger<SampleProcessor> logger) : BaseProcessor<SampleConfig>
{
    /// <inheritdoc/>
    protected override Task<List<ProcessItem>> ProcessAsync(
        string validatedData, SampleConfig? config, CancellationToken ct)
    {
        var baseNumber = config?.Number ?? 0;            // D-03 null-config default — warning-clean guard (Pitfall 2)
        var label      = config?.Label;                  // D-10 verbatim — already "Step_*", do NOT prepend

        // DELIBERATE multiple-execution stress test: ONE dispatch spawns TWO completed executions, each with an
        // independent random sum and its own author-minted ExecutionId. Exercises the pipeline's N-output slot-array
        // spawn path and the downstream fan-out multiplication. Processor-only change — the rest of the system
        // (orchestrator, keeper, analyzer, workflow, TTLs) is untouched, so the sweep verdicts are observational.
        var items = new List<ProcessItem>(2);
        for (var i = 0; i < 2; i++)
        {
            var sum  = baseNumber + Random.Shared.Next(0, 100);   // D-07 (0..99 inclusive); D-02 independent random per execution
            // D-04 {number,label} JSON SHAPE as a STRING (ProcessItem.Data is a string, Pitfall 1); D-05 shared options.
            var data = JsonSerializer.Serialize(
                new { number = sum, label },
                ProcessorConfig.SerializerOptions);
            // One structured log per execution; D-09 ids ambient (consume-filter scope); D-10 {StepLabel} verbatim;
            // T-18-04/V7 structured params, never interpolated.
            logger.LogInformation("step completed {StepLabel} sum {Sum}", label, sum);
            // D-06 author mints the per-item ExecutionId (distinct per execution).
            items.Add(new(ProcessOutcome.Completed, data, Guid.NewGuid()));
        }
        return Task.FromResult(items);
    }
}
