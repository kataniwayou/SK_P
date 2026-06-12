using System;

namespace BaseProcessor.Core.Configuration;

/// <summary>
/// Phase 57 Gate A (CFG-05) — the <c>schema ⊨ TConfig</c> covers-checker: given a fetched config-schema
/// definition (JSON Schema, Draft 2020-12) and the concrete <c>TConfig</c> CLR type, decide whether every
/// schema-valid payload would bind into <c>TConfig</c> under <see cref="ProcessorConfig.SerializerOptions"/>.
///
/// <para>
/// WAVE-0 STUB (Plan 57-01): this is the not-yet-implemented seam Plan 57-02 fills in with the real
/// JsonSchema.Net structural walk + the STJ Type-Clash Rule Table (grounded by the Wave-0 spike in
/// <c>ConfigSchemaCoverageFacts</c>). It currently honors ONLY the CFG-07 null-is-skip contract; every
/// non-null definition returns a placeholder "not-yet-implemented" clash so the table-driven
/// <c>Covers_Matches_RuleTable</c> theory is RED at runtime until Plan 02 lands the walk. Do NOT treat
/// the placeholder verdict as authoritative.
/// </para>
/// </summary>
internal static class ConfigSchemaCoverageCheck
{
    /// <summary>
    /// Evaluate whether <paramref name="configType"/> covers <paramref name="configDefinition"/>.
    /// </summary>
    /// <param name="configDefinition">The fetched config-schema definition (JSON Schema text), or null
    /// when the processor declares no <c>ConfigSchemaId</c> (CFG-07 skip).</param>
    /// <param name="configType">The concrete <c>TConfig</c> type to check against.</param>
    /// <returns><c>(Covered, ClashDetail)</c>. <c>null</c> definition → <c>(true, null)</c> (skip).
    /// An unparseable definition → <c>(false, "&lt;reason&gt;")</c> (terminal clash). A real type clash on
    /// a both-present property → <c>(false, "&lt;property + schema-type vs CLR-type&gt;")</c>.</returns>
    public static (bool Covered, string? ClashDetail) Evaluate(string? configDefinition, Type configType)
    {
        // CFG-07 — null ConfigSchemaId → no schema to validate against → covered (skip).
        if (configDefinition is null)
            return (true, null);

        // WAVE-0 STUB — Plan 02 replaces this with the JsonSchema.Net structural walk.
        return (false, "ConfigSchemaCoverageCheck not yet implemented (Plan 57-02).");
    }
}
