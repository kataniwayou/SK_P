namespace Messaging.Contracts.Hashing;

/// <summary>
/// req-1 / D-04: the SINGLE canonical hash path for the whole phase. Both the orchestrator and the
/// processor route every identity/content hash through THIS class so they cannot compute a different
/// <c>H</c> (or EntryId) for the same logical message. NO second canonicalization may exist anywhere
/// (D-04). Mirrors the build-time <c>SourceHash.targets</c> convention byte-for-byte:
/// UTF-8 -&gt; SHA-256 -&gt; lowercase <c>b.ToString("x2")</c> (^[a-f0-9]{64}$). Never the uppercase-hex
/// formatter nor the BCL hex-string converter (RESEARCH § "Don't Hand-Roll").
/// <para>
/// SHA-256 here is used for content-addressing / dedup identity, NOT security (no secret, no auth) —
/// collision-resistance is the only relied-on property (T-31-03). The reserved unit-separator
/// <c>U+001F</c> (D-03, Tampering mitigation T-31-01) can never appear in a Guid "D" rendering or in
/// lowercase hex, so the canonical field join is injection-free with a fixed field order.
/// </para>
/// </summary>
public static class MessageIdentity
{
    // ASCII unit separator (U+001F) — never appears in Guid "D" or lowercase hex (D-03 reserved separator, T-31-01).
    private const char Sep = '';

    private static string Hex(string canonicalText)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonicalText));
        var sb = new System.Text.StringBuilder(64);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));   // LOWERCASE x2 — never uppercase / BCL hex-string converter
        return sb.ToString();
    }

    /// <summary>
    /// req-1: the dedup identity <c>H</c> over the 5 identity fields. The per-execution lineage id is
    /// DELIBERATELY absent (D-02 — lineage only, excluded from H) so recomputing across runs is byte-identical.
    /// Fixed field order; each Guid rendered <c>g.ToString("D")</c> (invariant, lowercase by default).
    /// </summary>
    public static string ComputeH(Guid correlationId, Guid workflowId, Guid stepId, Guid processorId, string entryId)
        => Hex(string.Join(Sep, correlationId.ToString("D"), workflowId.ToString("D"), stepId.ToString("D"), processorId.ToString("D"), entryId));

    /// <summary>req-3: content-address a result blob through the SAME core path.</summary>
    public static string HashBlob(string blob) => Hex(blob);

    /// <summary>
    /// req-3 / D-08: content-address the manifest. The caller owns the serialization
    /// (<c>JsonSerializer.Serialize(string[])</c>) and passes the already-serialized JSON array text, so
    /// the hash is over the exact wire bytes and this leaf stays serialization-agnostic (no JSON dependency here).
    /// </summary>
    public static string HashManifest(string manifestJson) => Hex(manifestJson);

    /// <summary>req-2: the deterministic entry-step EntryId = hash(correlationId, stepId).</summary>
    public static string EntryEntryId(Guid correlationId, Guid stepId)
        => Hex(string.Join(Sep, correlationId.ToString("D"), stepId.ToString("D")));
}
