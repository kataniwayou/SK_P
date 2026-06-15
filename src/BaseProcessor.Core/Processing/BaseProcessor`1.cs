using System.Text.Json;
using BaseProcessor.Core.Configuration;

namespace BaseProcessor.Core.Processing;

/// <summary>
/// D-01/D-02: the generic framework layer between the non-generic <see cref="BaseProcessor"/> (the type
/// the pipeline resolves and calls) and the concrete author processor. It supplies the non-generic
/// <c>internal</c> <see cref="ExecuteAsync"/> body: it deserializes the dispatch <c>payload</c>
/// into <typeparamref name="TConfig"/> (D-05, one shared <see cref="ProcessorConfig.SerializerOptions"/>),
/// then invokes the author's typed <see cref="ProcessAsync"/> seam. A malformed payload throws
/// <see cref="System.Text.Json.JsonException"/>, which propagates UNCAUGHT to the pipeline catch-all at
/// ProcessorPipeline.cs:241 → one <c>StepFailed</c> (D-03). An empty/whitespace payload short-circuits to
/// a null config (D-04). The author overrides ONLY the typed <see cref="ProcessAsync"/> (BPC-02 invariant).
/// </summary>
public abstract class BaseProcessor<TConfig> : BaseProcessor
    where TConfig : ProcessorConfig   // reference-type/marker constraint → null representable (D-04, Pattern 4)
{
    internal sealed override Task<List<ProcessItem>> ExecuteAsync(
        string validatedData, string payload, Guid executionId, CancellationToken ct)
    {
        TConfig? config = string.IsNullOrWhiteSpace(payload)              // D-04 guard BEFORE deserialize
            ? null
            : JsonSerializer.Deserialize<TConfig>(payload, ProcessorConfig.SerializerOptions); // D-05; JsonException → :241 (D-03)
        return ProcessAsync(validatedData, config, executionId, ct);
    }

    /// <summary>
    /// The typed In-Process transform seam (D-02). The author overrides ONLY this. Receives the
    /// input-schema-validated L2 blob (<paramref name="validatedData"/>, still a raw string — input typing
    /// is out of scope) and the framework-deserialized <paramref name="config"/> (null when the payload was
    /// empty/whitespace/absent). <paramref name="executionId"/> is the inbound dispatch's per-instance id:
    /// <c>Guid.Empty</c> means ENTRY/seed (mint fresh ids per spawned execution); a non-empty value means
    /// DOWNSTREAM (reuse it unchanged to preserve the instance lineage). May THROW a
    /// <c>ProcessStatusException</c> to abort the batch.
    /// </summary>
    protected abstract Task<List<ProcessItem>> ProcessAsync(
        string validatedData, TConfig? config, Guid executionId, CancellationToken ct);
}
