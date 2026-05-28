namespace BaseApi.Tests.TestHelpers;

/// <summary>
/// Shared test-only hash utilities.
/// <para>
/// Extracted in Phase 10 IN-04 — the same <see cref="RandomSha256Hex"/> helper was
/// previously duplicated verbatim across 8 test files (the canonical copy lived in
/// <c>tests/BaseApi.Tests/Integration/ProcessorsIntegrationTests.cs</c>). Centralising
/// keeps single-edit ergonomics if the implementation ever shifts (e.g., to a
/// cryptographically strong RNG or <c>Convert.ToHexString</c>).
/// </para>
/// </summary>
internal static class HashHelpers
{
    /// <summary>
    /// Generates a unique 64-char lowercase hex string suitable for use as a
    /// <c>ProcessorEntity.SourceHash</c> seed value. Uses two concatenated
    /// <see cref="Guid.NewGuid"/> byte sequences (32 bytes total) — no security
    /// properties are claimed; the only requirement is uniqueness across
    /// sibling tests sharing the <c>uq_processor_source_hash</c> unique index.
    /// </summary>
    public static string RandomSha256Hex()
    {
        var bytes = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
        return string.Concat(bytes.Select(b => b.ToString("x2")));
    }
}
