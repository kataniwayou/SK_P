using BaseProcessor.Core.Processing;
using Microsoft.Extensions.Logging;

namespace Processor.Sample;

/// <summary>
/// The one concrete <see cref="BaseProcessor{SampleConfig}"/> (SAMPLE-01 / D-07). It overrides ONLY the
/// typed In-Process <c>ProcessAsync</c> seam; the framework owns deserialization (it hands in a typed
/// <see cref="SampleConfig"/>) and the entire Pre/Post/end-delete pipeline. The transform echoes the
/// config's <c>Value</c> as a single completed <see cref="ProcessItem"/> (minting its own ExecutionId,
/// D-03). A null config (empty/absent payload) falls back to the fixed "processor-sample-ok" token; a
/// Value of "fail" demonstrates the author status-exception path (throws <c>FailedException</c>).
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
        var value = config?.Value;                       // D-04: null config → null value
        logger.LogInformation("sample payload received: {Payload}", value);

        // D-09: a "fail" value aborts the batch with a business Failed (status-exception path).
        if (value == "fail")
            throw new FailedException("sample reason");

        // One completed item; the author MINTS the ExecutionId (D-03).
        return Task.FromResult(new List<ProcessItem>
        {
            new(ProcessOutcome.Completed, value ?? "processor-sample-ok", Guid.NewGuid()),
        });
    }
}
