using System.Text.Json;
using BaseProcessor.Core.Processing;
using Microsoft.Extensions.Logging;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace Processor.Sample;

/// <summary>
/// The one concrete <see cref="BaseProcessorBase"/> (SAMPLE-01 / D-07). It overrides ONLY the Phase-44
/// In-Process <c>ProcessAsync</c> seam; the framework (<c>ProcessorPipeline</c>) owns the entire
/// Pre/Post/end-delete pipeline (retry, L2 read/write/delete, all Keeper + Step* sends, the framework
/// entryId). The transform deserializes the dispatch <c>payload</c> — the per-step assignment payload —
/// logs it, and echoes it back as a single completed <see cref="ProcessItem"/> (MINTING its own
/// <c>ExecutionId</c>, D-03) so the live round-trip can prove (via ES logs) that each step received its
/// own assigned payload. An absent/blank payload falls back to the fixed <c>"processor-sample-ok"</c>
/// token. A <c>"fail"</c> payload demonstrates the status-exception path (the author may THROW a
/// <c>ProcessStatusException</c> to abort the batch with a business status).
/// </summary>
/// <remarks>
/// The base type is aliased (<c>BaseProcessorBase</c>) because the unqualified name
/// <c>BaseProcessor</c> binds to the <c>BaseProcessor.Core</c> root namespace, not the type (CS0118).
/// The <see cref="ILogger{TCategoryName}"/> is DI-resolved — the concrete is registered
/// <c>AddSingleton&lt;BaseProcessorBase, SampleProcessor&gt;</c> and OTel logging supplies the logger.
/// </remarks>
public sealed class SampleProcessor(ILogger<SampleProcessor> logger) : BaseProcessorBase
{
    /// <inheritdoc/>
    protected override Task<List<ProcessItem>> ProcessAsync(
        string validatedData, string payload, CancellationToken ct)
    {
        // payload IS the dispatch config (the step's assignment payload), a JSON string e.g. "\"StepA1\"".
        var cfg = string.IsNullOrWhiteSpace(payload)
            ? null
            : JsonSerializer.Deserialize<string>(payload);

        logger.LogInformation("sample payload received: {Payload}", cfg);

        // Demonstrate the author status-exception path (D-04): a "fail" payload aborts with a business Failed.
        if (cfg == "fail")
            throw new FailedException("sample reason");

        // One completed item; the author MINTS the ExecutionId (D-03).
        return Task.FromResult(new List<ProcessItem>
        {
            new(ProcessOutcome.Completed, cfg ?? "processor-sample-ok", Guid.NewGuid()),
        });
    }
}
