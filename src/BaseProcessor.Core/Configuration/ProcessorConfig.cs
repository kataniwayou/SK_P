using System.Text.Json;

namespace BaseProcessor.Core.Configuration;

/// <summary>
/// SPEC Req 1 / D-08: the empty marker base config — ZERO framework-mandated fields; a pure type
/// anchor that the author config record inherits and that Phase 57 Gate A compares against the
/// config-schema definition. Authors add all their own fields on the derived record.
/// </summary>
public abstract record ProcessorConfig
{
    /// <summary>
    /// D-06: the SINGLE canonical config-deserialization contract. Phase 57 Gate A reuses THIS
    /// instance so it gates against exactly the deserialize behavior the framework runs
    /// (D-05: case-insensitive; unknown JSON properties IGNORED — NOT JsonUnmappedMemberHandling.Disallow).
    /// One cached static — never `new JsonSerializerOptions()` per call.
    /// </summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,   // D-05
        // default unknown-member handling = ignore (do NOT set JsonUnmappedMemberHandling.Disallow) — D-05
    };
}
