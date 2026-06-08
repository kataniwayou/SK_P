namespace BaseProcessor.Core.Processing;

/// <summary>D-03: the unit returned by the author's ProcessAsync. The author constructs it directly,
/// declares the per-item <see cref="Result"/> (Completed|Failed), supplies the output <see cref="Data"/>,
/// and MINTS <see cref="ExecutionId"/> itself (a new GUID per item — A8/A12). Replaces the old
/// framework-owned <c>ProcessResult</c>.</summary>
public sealed record ProcessItem(ProcessOutcome Result, string Data, Guid ExecutionId);
