using BaseProcessor.Core.Processing;
using Microsoft.Extensions.Logging;

namespace Processor.BadConfig;

/// <summary>
/// The one concrete <see cref="BaseProcessor{BadConfig}"/> (CFG-08 subject). It overrides ONLY the
/// typed In-Process <c>ProcessAsync</c> seam; the framework owns deserialization and the entire
/// Pre/Post/end-delete pipeline. This transform body is dead-but-must-compile: Gate A's clash
/// (<see cref="BadConfig"/> int Quantity vs schema-string) withholds <c>MarkHealthy</c>, so no
/// dispatch queue is bound and <c>ProcessAsync</c> is never reached. A trivial single-item
/// Completed return suffices to satisfy the abstract contract.
/// </summary>
/// <remarks>
/// DI-registered <c>AddSingleton&lt;BaseProcessor, BadConfigProcessor&gt;</c> (Program.cs) —
/// BadConfigProcessor IS-A BaseProcessor via BaseProcessor&lt;BadConfig&gt;.
/// </remarks>
public sealed class BadConfigProcessor(ILogger<BadConfigProcessor> logger) : BaseProcessor<BadConfig>
{
    /// <inheritdoc/>
    protected override Task<List<ProcessItem>> ProcessAsync(
        string validatedData, BadConfig? config, CancellationToken ct)
    {
        // Dead path — Gate A withholds the queue bind so this is never invoked. Trivial completed item.
        logger.LogWarning("badconfig transform invoked (unexpected — Gate A should have withheld health)");

        return Task.FromResult(new List<ProcessItem>
        {
            new(ProcessOutcome.Completed, "processor-badconfig-ok", Guid.NewGuid()),
        });
    }
}
