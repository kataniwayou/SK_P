using System.Text.Json;                  // JsonSerializer — NOT in .NET 8 implicit usings
using BaseProcessor.Core.Configuration;  // ProcessorConfig.SerializerOptions
using BaseProcessor.Core.Processing;
using Microsoft.Extensions.Logging;

namespace Processor.Sample;

/// <summary>
/// The one concrete <see cref="BaseProcessor{SampleConfig}"/> (PROC-02/PROC-03). It overrides ONLY the
/// typed In-Process <c>ProcessAsync</c> seam; the framework owns deserialization (it hands in a typed
/// <see cref="SampleConfig"/>) and the entire Pre/Post/end-delete pipeline. The transform random-adds to
/// the config's <c>Number</c> and emits the resulting <c>sum</c> as a single completed
/// <see cref="ProcessItem"/> (a {number,label} JSON string, minting its own ExecutionId, D-06), then emits
/// exactly ONE structured log entry tagged with the <c>Step_*</c> label and the computed sum (D-08). The
/// 6 correlation ids attach automatically from the ambient consume-filter scope (D-09) — none are added here.
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
        var sum        = baseNumber + Random.Shared.Next(0, 100);   // D-07 (0..99 inclusive); D-02 random sum

        // D-04 JSON object SHAPE produced as a STRING (ProcessItem.Data is a string, Pitfall 1);
        // D-05 shared options (symmetric with the seam's Deserialize); lowercase members → {"number":…,"label":…}
        var data = JsonSerializer.Serialize(
            new { number = sum, label },
            ProcessorConfig.SerializerOptions);

        // D-08 exactly one entry; D-09 ids are ambient (consume-filter scope), NOT params here;
        // D-10 {StepLabel}=label verbatim; T-18-04/V7 structured params, never interpolated.
        logger.LogInformation("step completed {StepLabel} sum {Sum}", label, sum);

        // D-06 author mints the per-item ExecutionId.
        return Task.FromResult(new List<ProcessItem>
        {
            new(ProcessOutcome.Completed, data, Guid.NewGuid()),
        });
    }
}
