using BaseProcessor.Core.Processing;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace Processor.Sample;

/// <summary>
/// The one concrete <see cref="BaseProcessorBase"/> (SAMPLE-01 / D-04). It overrides ONLY the
/// <c>ProcessAsync</c> transform seam; the framework (AddBaseProcessor) owns all id-minting,
/// validation, L2 read/write, dispatch, and result sending. This POC transform returns a single
/// fixed deterministic result — multi-result is already unit-proven in Phase 27, so the live
/// round-trip needs only one. No infra / DI / id / L2 / bus code lives here (BPC-02).
/// </summary>
/// <remarks>
/// The base type is aliased (<c>BaseProcessorBase</c>) because the unqualified name
/// <c>BaseProcessor</c> binds to the <c>BaseProcessor.Core</c> root namespace, not the type (CS0118).
/// </remarks>
public sealed class SampleProcessor : BaseProcessorBase
{
    /// <inheritdoc/>
    protected override Task<IReadOnlyList<ProcessResult>> ProcessAsync(
        string inputData, string config, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProcessResult>>(
            new[] { new ProcessResult("processor-sample-ok") });
}
