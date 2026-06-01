namespace BaseProcessor.Core.Processing;

/// <summary>
/// The abstract processor base (D-12 / BPC-02). It declares exactly one seam — the locked
/// <see cref="ProcessAsync"/> transform — so a concrete <c>Processor.&lt;Purpose&gt;</c> overrides
/// only that method; the framework owns all id-minting, validation, L2 read/write, and result
/// sending (Phase 27).
///
/// <para>
/// The seam is DECLARED this phase, INVOKED in Phase 27. A test double overrides it to prove the
/// class compiles and DI-resolves. The signature is locked by PROJECT.md:32 —
/// <c>Task&lt;IReadOnlyList&lt;ProcessResult&gt;&gt; ProcessAsync(string inputData, string config, CancellationToken ct)</c>.
/// </para>
/// </summary>
public abstract class BaseProcessor
{
    /// <summary>
    /// Transforms <paramref name="inputData"/> (read from L2, never the dispatch payload) using
    /// <paramref name="config"/> (the dispatch payload) into zero or more <see cref="ProcessResult"/>.
    /// Declared now; invoked by the Phase 27 execution round-trip.
    /// </summary>
    protected abstract Task<IReadOnlyList<ProcessResult>> ProcessAsync(
        string inputData, string config, CancellationToken ct);
}
