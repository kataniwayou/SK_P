namespace BaseProcessor.Core.Processing;

/// <summary>D-04: the author throws one of the three concrete subclasses from ProcessAsync to abort the
/// whole batch with a status. The pipeline does <c>catch (ProcessStatusException e)</c> and maps the
/// runtime type to the matching Step* record (FailedException→StepFailed, CancelledException→StepCancelled,
/// ProcessingException→StepProcessing). D-05: all three accept an author message; StepProcessing has no
/// wire message field so the processing message is logged only — but the author-facing API is uniform.</summary>
public abstract class ProcessStatusException(string message) : Exception(message);

/// <summary>D-04: author signals an in-flight "processing" status (maps to StepProcessing; message logged only).</summary>
public sealed class ProcessingException(string message) : ProcessStatusException(message);

/// <summary>D-04: author signals a business "failed" status (maps to StepFailed; message → StepFailed.ErrorMessage).</summary>
public sealed class FailedException(string message) : ProcessStatusException(message);

/// <summary>D-04: author signals a "cancelled" status (maps to StepCancelled; message → StepCancelled.CancellationMessage).</summary>
public sealed class CancelledException(string message) : ProcessStatusException(message);
