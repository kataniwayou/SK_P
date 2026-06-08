namespace BaseProcessor.Core.Processing;

/// <summary>
/// The abstract processor base (D-12 / BPC-02). It declares exactly one seam — the locked
/// <see cref="ProcessAsync"/> transform — so a concrete <c>Processor.&lt;Purpose&gt;</c> overrides
/// only that method; the framework owns the entire Pre / Post / end-delete pipeline (retry, L2
/// read/write/delete, all Keeper + Step* sends, id-minting of the framework entryId), leaving the
/// author ONLY the In-Process transform (Phase 44, D-01/D-02/D-03).
///
/// <para>
/// The author override deserializes BOTH raw-JSON strings — the already-input-schema-validated L2
/// blob (<c>validatedData</c>) and the dispatch <c>payload</c> (config) — and returns a
/// <see cref="List{ProcessItem}"/> declaring the per-item outcome (Completed|Failed) and MINTING the
/// per-item <c>ExecutionId</c> itself (D-03). To abort the whole batch with a business status the
/// author THROWS one of the <c>ProcessStatusException</c> family (processing/failed/cancelled), which
/// the pipeline maps by runtime type to the matching <c>Step*</c> record (D-04/D-05).
/// </para>
/// </summary>
public abstract class BaseProcessor
{
    /// <summary>
    /// The In-Process transform seam (D-01/D-02/D-03). Author overrides ONLY this. Receives the
    /// input-schema-validated L2 blob (<paramref name="validatedData"/>, never the dispatch payload)
    /// and the dispatch <paramref name="payload"/> (config) — both raw JSON the author deserializes —
    /// and returns the per-item <see cref="ProcessItem"/> list (the author declares each item's outcome
    /// and mints its <c>ExecutionId</c>). May THROW a <c>ProcessStatusException</c> to abort the batch
    /// with a business status.
    /// </summary>
    protected abstract Task<List<ProcessItem>> ProcessAsync(
        string validatedData, string payload, CancellationToken ct);

    /// <summary>
    /// Framework-internal invoker (EXEC-04). The Phase 44 <c>ProcessorPipeline</c> lives in this SAME
    /// assembly and so cannot call the <c>protected</c> ProcessAsync of a non-derived instance; this
    /// <c>internal</c> forwarder is the seam it calls. The concrete (a different assembly) never sees
    /// this method and still overrides ONLY ProcessAsync (BPC-02).
    /// </summary>
    internal Task<List<ProcessItem>> ExecuteAsync(string validatedData, string payload, CancellationToken ct)
        => ProcessAsync(validatedData, payload, ct);
}
