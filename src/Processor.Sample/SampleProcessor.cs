using System.Text.Json;
using BaseProcessor.Core.Processing;
using Microsoft.Extensions.Logging;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace Processor.Sample;

/// <summary>
/// The one concrete <see cref="BaseProcessorBase"/> (SAMPLE-01 / D-04). It overrides ONLY the
/// <c>ProcessAsync</c> transform seam; the framework (AddBaseProcessor) owns all id-minting,
/// validation, L2 read/write, dispatch, and result sending. The transform now consumes the
/// dispatch <c>config</c> — the per-step assignment payload — by deserializing the JSON string,
/// logging it, and echoing it back as the single <see cref="ProcessResult"/> so the live
/// round-trip can prove (via ES logs) that each step received its own assigned payload. An
/// absent/blank payload falls back to the original fixed <c>"processor-sample-ok"</c> token, so a
/// no-assignment dispatch still produces a deterministic result.
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
    protected override Task<IReadOnlyList<ProcessResult>> ProcessAsync(
        string inputData, string config, CancellationToken ct)
    {
        // config IS the dispatch payload (the step's assignment payload), a JSON string e.g. "\"StepA1\"".
        var payload = string.IsNullOrWhiteSpace(config)
            ? null
            : JsonSerializer.Deserialize<string>(config);

        logger.LogInformation("sample payload received: {Payload}", payload);

        return Task.FromResult<IReadOnlyList<ProcessResult>>(
            new[] { new ProcessResult(payload ?? "processor-sample-ok") });
    }
}
