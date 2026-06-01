namespace BaseProcessor.Core.Processing;

/// <summary>
/// The unit of output produced by <see cref="BaseProcessor.ProcessAsync"/> (D-08 / D-10 / BPC-02).
/// Carries ONLY the output-data string: the concrete's transform produces output content, it does
/// NOT carry or own an outcome. The framework owns ALL outcomes (Completed/Failed/Cancelled) —
/// per result it output-validates this <see cref="OutputData"/>, mints a new entryId, writes it to
/// <c>L2[data(newEntryId)]</c>, and builds the ExecutionResult. Do NOT add an outcome field here.
/// </summary>
public sealed record ProcessResult(string OutputData);
