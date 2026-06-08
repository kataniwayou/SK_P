namespace BaseProcessor.Core.Processing;

/// <summary>D-03: per-item author-declared outcome. The author distinguishes a completed transform
/// result from a business failure; infra outcomes are framework-derived, never author-declared.</summary>
public enum ProcessOutcome { Completed, Failed }
