using BaseProcessor.Core.Configuration;

namespace Processor.Sample;

/// <summary>
/// SAMPLE-01 / D-08: the minimal author config — a single nullable field, deriving from the empty
/// framework marker <see cref="ProcessorConfig"/>. The framework deserializes the dispatch payload
/// (object shape, e.g. {"value":"StepA1"}) into this record before invoking the typed transform.
/// </summary>
public sealed record SampleConfig(string? Value) : ProcessorConfig;
