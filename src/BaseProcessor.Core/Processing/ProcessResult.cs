namespace BaseProcessor.Core.Processing;

/// <summary>
/// The unit of output produced by <see cref="BaseProcessor.ProcessAsync"/> (D-12 / BPC-02).
/// Declared now as a minimal positional record (mirrors the shared L2 projection record shape);
/// the concrete fields (output data + per-result identifiers) are firmed up in Phase 27 when the
/// seam is actually invoked by the framework.
/// </summary>
public sealed record ProcessResult();
