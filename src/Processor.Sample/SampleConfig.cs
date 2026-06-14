using BaseProcessor.Core.Configuration;

namespace Processor.Sample;

/// <summary>
/// PROC-01 / D-01: the author config — an integer + a nullable string, deriving from the empty
/// framework marker <see cref="ProcessorConfig"/>. The framework deserializes the per-step assignment
/// payload (e.g. {"number":5,"label":"Step_A1"}) into this record (case-insensitive, via
/// <see cref="ProcessorConfig.SerializerOptions"/>) before invoking the typed transform.
/// </summary>
public sealed record SampleConfig(int Number, string? Label) : ProcessorConfig;
