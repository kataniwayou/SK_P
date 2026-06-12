using BaseProcessor.Core.Configuration;

namespace Processor.BadConfig;

/// <summary>
/// CFG-08 / D-02 (RESEARCH/PATTERNS Option A): the deliberate clash TConfig. The CLR types
/// <c>Quantity</c> as <see cref="int"/>; the seeded config-schema (Plan 02/04) types
/// <c>"quantity"</c> as <c>"string"</c>, so
/// <c>ConfigSchemaCoverageCheck.ClassifyScalar</c>'s String-case returns
/// <c>Detail("quantity","string",Int32)</c> (ConfigSchemaCoverageCheck.cs ~line 213) →
/// <c>(Covered:false)</c> → Gate A withholds <c>MarkHealthy</c> at startup, so no
/// <c>skp:{id}</c> liveness key is written and orchestration-start blocks any workflow using
/// this processor with 422.
/// </summary>
public sealed record BadConfig(int Quantity) : ProcessorConfig;
