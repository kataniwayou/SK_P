using System.Collections.Generic;
using System.Text.Json;
using BaseProcessor.Core.Configuration;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 57 Gate A (CFG-05) facts. Two parts:
///
/// <para>
/// PART A — the BLOCKING Wave-0 STJ spike. Three (four) <see cref="FactAttribute"/> methods drive
/// crafted JSON through the EXACT cached <see cref="ProcessorConfig.SerializerOptions"/> instance the
/// framework deserializes with, to EMPIRICALLY ground the three highest-risk
/// <c>[ASSUMED]</c> verdicts in RESEARCH §"STJ Type-Clash Rule Table" (rows #13 / #5+#8 / #22):
/// <list type="bullet">
///   <item>A1 (row #13): string-enum JSON value -&gt; CLR <c>enum</c> — does it CLASH (no
///         JsonStringEnumConverter registered)?</item>
///   <item>A2 (rows #5/#8): JSON number with a fraction -&gt; CLR <c>int</c>, and JSON string -&gt;
///         CLR numeric — do they CLASH under the default (strict) NumberHandling?</item>
///   <item>A3 (row #22): JSON <c>null</c> -&gt; non-nullable CLR value type — does it CLASH?</item>
/// </list>
/// These facts run GREEN immediately (they assert the OBSERVED deserialize behavior, depending on no
/// not-yet-written production type). They are the PERMANENT ground-truth for the rule table and MUST NOT
/// be deleted when Plan 02 lands the checker.
/// </para>
///
/// <para>
/// PART B — the table-driven covers-check tests. They reference
/// <c>ConfigSchemaCoverageCheck.Evaluate</c> (the Plan-02 signature) which does NOT exist yet, so this
/// file does NOT compile — that is the intended RED state (Nyquist: the Wave-1/2 task that creates the
/// checker has a pre-existing automated verify to turn GREEN).
/// </para>
/// </summary>
public sealed class ConfigSchemaCoverageFacts
{
    // ---------------------------------------------------------------------------------------------
    // PART A — the STJ spike. Local positional records inheriting ProcessorConfig, deserialized
    // through the REAL ProcessorConfig.SerializerOptions (case-insensitive, ignore-unknown, NO
    // naming policy, NO NumberHandling, NO JsonStringEnumConverter).
    // ---------------------------------------------------------------------------------------------

    private enum SpikeEnum
    {
        A,
        B,
    }

    private sealed record EnumConfig(SpikeEnum Mode) : ProcessorConfig;

    private sealed record IntConfig(int N) : ProcessorConfig;

    /// <summary>
    /// A1 / rule-table row #13 — the single highest-value clash. A JSON string <c>"A"</c> is valid under
    /// a string-enum schema; binding it to a CLR <c>enum</c> through the real options (NO
    /// JsonStringEnumConverter registered) is expected to THROW — confirming #13 is a CLASH.
    /// </summary>
    [Fact]
    public void Spike_A1_StringEnumSchema_To_ClrEnum()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<EnumConfig>("{\"Mode\":\"A\"}", ProcessorConfig.SerializerOptions));
    }

    /// <summary>
    /// A2a / rule-table row #5 — a schema <c>type: number</c> admits <c>3.14</c>; binding a fractional
    /// JSON number to CLR <c>int</c> through the real options is expected to THROW — confirming #5 CLASH.
    /// </summary>
    [Fact]
    public void Spike_A2a_NumberSchema_To_Int()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<IntConfig>("{\"N\":3.14}", ProcessorConfig.SerializerOptions));
    }

    /// <summary>
    /// A2b / rule-table row #8 — a JSON string <c>"abc"</c> is valid under a <c>type: string</c> schema;
    /// binding it to CLR <c>int</c> (NO NumberHandling.AllowReadingFromString set) is expected to THROW —
    /// confirming #8 CLASH.
    /// </summary>
    [Fact]
    public void Spike_A2b_StringToNumber()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<IntConfig>("{\"N\":\"abc\"}", ProcessorConfig.SerializerOptions));
    }

    /// <summary>
    /// A3 / rule-table row #22 — a nullable-union schema (<c>["integer","null"]</c>) admits <c>null</c>;
    /// binding <c>null</c> to a non-nullable CLR value type (<c>int</c>) through the real options is
    /// expected to THROW — confirming #22 CLASH.
    /// </summary>
    [Fact]
    public void Spike_A3_Null_To_NonNullableValueType()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<IntConfig>("{\"N\":null}", ProcessorConfig.SerializerOptions));
    }

    // ---------------------------------------------------------------------------------------------
    // PART B — the table-driven covers-check (RED: ConfigSchemaCoverageCheck.Evaluate does not exist
    // yet — Plan 02 creates it with signature
    //   public static (bool Covered, string? ClashDetail) Evaluate(string? configDefinition, Type configType)
    // ). Each row cites its rule-table # in a comment. null definition -> (true, null) [CFG-07 skip];
    // unparseable definition -> (false, "<reason>") [terminal clash].
    // ---------------------------------------------------------------------------------------------

    // Local config records used as the `Type` argument for the covers rows.
    private sealed record StringConfig(string S) : ProcessorConfig;

    private sealed record IntCoverConfig(int N) : ProcessorConfig;

    private sealed record NullableIntConfig(int? N) : ProcessorConfig;

    private sealed record ListConfig(List<int> Items) : ProcessorConfig;

    private sealed record NestedInner(int N);

    private sealed record NestedConfig(NestedInner Inner) : ProcessorConfig;

    private const string SchemaPrefix = "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",";

    public static IEnumerable<object[]> CoverRows()
    {
        // ---- covered = true ----
        // #7 string <-> string
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"S\":{\"type\":\"string\"}}}", typeof(StringConfig), true };
        // #1 integer <-> int
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"N\":{\"type\":\"integer\"}}}", typeof(IntCoverConfig), true };
        // #14 string-enum <-> string
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"S\":{\"enum\":[\"A\",\"B\"]}}}", typeof(StringConfig), true };
        // #21 nullable-union <-> int?
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"N\":{\"type\":[\"integer\",\"null\"]}}}", typeof(NullableIntConfig), true };
        // #23 no-type keyword (unconstrained) -> any CLR type
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"N\":{}}}", typeof(IntCoverConfig), true };
        // #24 schema-only prop (not on TConfig) -> ignored at runtime
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"Other\":{\"type\":\"string\"}}}", typeof(StringConfig), true };
        // #25 TConfig-only prop (not in schema) -> harmless
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{}}", typeof(StringConfig), true };
        // #19 nested object recurse (both-present, compatible)
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"Inner\":{\"type\":\"object\",\"properties\":{\"N\":{\"type\":\"integer\"}}}}}", typeof(NestedConfig), true };
        // #16 array <-> List<T> recurse (items integer <-> int)
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"Items\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}}}", typeof(ListConfig), true };

        // ---- covered = false ----
        // #13 string-enum <-> CLR enum (the highest-value clash)
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"Mode\":{\"enum\":[\"A\",\"B\"]}}}", typeof(EnumConfig), false };
        // #5 number <-> int
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"N\":{\"type\":\"number\"}}}", typeof(IntCoverConfig), false };
        // #8 string <-> int
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"N\":{\"type\":\"string\"}}}", typeof(IntCoverConfig), false };
        // #17 array <-> scalar
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"N\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}}}", typeof(IntCoverConfig), false };
        // #20 object <-> scalar
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"N\":{\"type\":\"object\"}}}", typeof(IntCoverConfig), false };
        // #22 nullable-union <-> non-nullable int
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"N\":{\"type\":[\"integer\",\"null\"]}}}", typeof(IntCoverConfig), false };
        // #26 union[string,integer] <-> int
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"N\":{\"type\":[\"string\",\"integer\"]}}}", typeof(IntCoverConfig), false };
        // nested clash: inner both-present prop clashes (schema string vs CLR int)
        yield return new object[] { SchemaPrefix + "\"type\":\"object\",\"properties\":{\"Inner\":{\"type\":\"object\",\"properties\":{\"N\":{\"type\":\"string\"}}}}}", typeof(NestedConfig), false };
        // unparseable definition string -> terminal clash
        yield return new object[] { "this is not json schema {", typeof(IntCoverConfig), false };
    }

    [Theory]
    [MemberData(nameof(CoverRows))]
    public void Covers_Matches_RuleTable(string definition, System.Type type, bool expectedCovered)
    {
        var (covered, detail) = ConfigSchemaCoverageCheck.Evaluate(definition, type);
        Assert.Equal(expectedCovered, covered);
        if (!expectedCovered)
            Assert.NotNull(detail);
    }

    /// <summary>CFG-07 — a null config definition is the skip case: covered, no clash detail.</summary>
    [Fact]
    public void NullDefinition_Is_Skip_Covered()
    {
        var (covered, detail) = ConfigSchemaCoverageCheck.Evaluate(null, typeof(IntCoverConfig));
        Assert.True(covered);
        Assert.Null(detail);
    }
}
