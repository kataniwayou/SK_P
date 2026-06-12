namespace BaseProcessor.Core.Processing;

/// <summary>
/// The non-generic processor base (D-01 / BPC-02). It declares the framework-internal
/// <see cref="ExecuteAsync"/> seam that the <c>ProcessorPipeline</c> resolves and calls — the pipeline
/// lives in THIS assembly and references the non-generic type, so the seam stays here and stays
/// signature-stable (string,string,ct). The typed author transform seam lives on the generic
/// <see cref="BaseProcessor{TConfig}"/>, which supplies this method's body by deserializing the
/// dispatch payload into the author config type before invoking the author's typed <c>ProcessAsync</c>.
/// A concrete <c>Processor.&lt;Purpose&gt;</c> derives from <see cref="BaseProcessor{TConfig}"/> and
/// overrides ONLY the typed transform; it never sees this <c>internal</c> seam (BPC-02).
/// </summary>
public abstract class BaseProcessor
{
    /// <summary>
    /// Framework-internal invoker (EXEC-04 / D-01). The Phase 44 <c>ProcessorPipeline</c> lives in this
    /// SAME assembly and references the non-generic <see cref="BaseProcessor"/>; this <c>internal abstract</c>
    /// seam is the method it calls. <see cref="BaseProcessor{TConfig}"/> supplies the body (deserialize
    /// the <paramref name="payload"/> into the typed config, then dispatch to the author transform).
    /// </summary>
    internal abstract Task<List<ProcessItem>> ExecuteAsync(
        string validatedData, string payload, CancellationToken ct);
}
