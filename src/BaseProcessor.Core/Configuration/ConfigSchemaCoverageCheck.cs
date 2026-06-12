using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;

namespace BaseProcessor.Core.Configuration;

/// <summary>
/// Phase 57 Gate A (CFG-05) — the <c>schema ⊨ TConfig</c> covers-checker. Given a fetched config-schema
/// definition (JSON Schema, Draft 2020-12) and the concrete <c>TConfig</c> CLR type, decide whether every
/// schema-valid payload would bind into <c>TConfig</c> under <see cref="ProcessorConfig.SerializerOptions"/>
/// (case-insensitive, ignore-unknown, NO naming policy, NO <c>NumberHandling</c>, NO
/// <c>JsonStringEnumConverter</c>). This is the reverse direction of Gate B
/// (<c>PayloadConfigSchemaValidator</c>, which is <c>payload ⊨ schema</c>); the two share only the
/// <see cref="JsonSchema.FromText(string, Json.Schema.BuildOptions, Uri, System.Nullable{JsonDocumentOptions})"/>
/// parse + the null-is-skip convention (CFG-07).
///
/// <para>
/// <b>Structural walk (D-01/D-04):</b> the check is a pure structural walk — it parses the definition,
/// enumerates the schema's declared <c>properties</c> / <c>type</c> / <c>enum</c> / <c>items</c>, reflects
/// <c>TConfig</c> honoring <c>[JsonPropertyName]</c> + <c>OrdinalIgnoreCase</c>, and flags a clash ONLY for a
/// property present in BOTH the schema and <c>TConfig</c> whose schema-valid values would actually fail to
/// deserialize (D-02). A schema-only property (ignore-unknown) and a <c>TConfig</c>-only property are FINE.
/// It recurses into nested objects and array element schemas.
/// </para>
///
/// <para>
/// <b>SSRF-safe (T-57-03):</b> the walk NEVER calls <see cref="JsonSchema.Evaluate"/> and never resolves an
/// external <c>$ref</c>. The fetched config-schema is operator-authored and meta-schema-validated on write
/// (VALID-08) and frozen-once-referenced (Plan 04), so the walk follows declared <c>properties</c>/<c>items</c>
/// only — no remote document fetch. Because there is no <c>Evaluate</c>, the <c>JsonSchemaConfig</c> global
/// <c>SchemaRegistry.Global.Fetch</c> lockdown is not even on this path (RESEARCH Pitfall 3).
/// </para>
///
/// <para>
/// <b>API note (Plan 02 deviation):</b> the rule-table verdicts for the high-risk rows #13 (string-enum →
/// CLR enum), #5/#8 (number/string → int), and #22 (nullable-union null → non-nullable value type) are the
/// spike-CONFIRMED CLASH verdicts from Plan 57-01's <c>ConfigSchemaCoverageFacts</c> Wave-0 spike (no
/// corrections were required). JsonSchema.Net 9.2.1 removed the keyword object model + per-keyword accessors
/// (<c>GetProperties()</c>/<c>GetType()</c>/<c>GetEnum()</c>/<c>GetItems()</c>) the research assumed; a parsed
/// <see cref="JsonSchema"/> is opaque in 9.x. Since a JSON Schema is itself JSON, the walk introspects the
/// definition as a <see cref="JsonNode"/> tree (the keywords are read via <see cref="SchemaKeywords"/>),
/// while <see cref="JsonSchema.FromText"/> remains the parse/validity gate shared with Gate B.
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
    /// An unparseable definition → <c>(false, "&lt;reason&gt;")</c> (terminal clash, never throws). A real
    /// type clash on a both-present property → <c>(false, "&lt;property + schema-type vs CLR-type&gt;")</c>.</returns>
    public static (bool Covered, string? ClashDetail) Evaluate(string? configDefinition, Type configType)
    {
        // CFG-07 — null ConfigSchemaId → no schema to validate against → covered (skip).
        // Mirrors PayloadConfigSchemaValidator.cs:42 and ProcessorStartupOrchestrator.cs:127-128.
        if (configDefinition is null)
            return (true, null);

        // Parse-validity gate, shared with Gate B (PayloadConfigSchemaValidator.cs:55). An unparseable
        // config-schema is its own terminal clash (CFG-06) — return Covered=false, NEVER throw.
        JsonNode? root;
        try
        {
            // JsonSchema.FromText is the same parse Gate B uses: it confirms the text is a structurally
            // valid JSON Schema (throws JsonException/JsonSchemaException otherwise).
            _ = JsonSchema.FromText(configDefinition);
            // JsonSchema 9.2.1 is opaque (no keyword accessors), so we read the same text as a JsonNode
            // tree to walk its declared keywords. A schema that FromText accepted parses as JSON here too.
            root = JsonNode.Parse(configDefinition);
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException)
        {
            return (false, $"config schema is not parseable: {ex.Message}");
        }

        if (root is not JsonObject schemaObject)
            return (true, null); // a boolean schema (true/false) declares no properties — nothing to clash.

        var clash = WalkObject(schemaObject, configType);
        return (clash is null, clash);
    }

    /// <summary>
    /// Walk an object-schema's declared properties against a CLR type. Returns the first clash detail, or
    /// null when every both-present property is bind-compatible. Schema-only properties (row #24) and
    /// TConfig-only properties (row #25) are FINE.
    /// </summary>
    private static string? WalkObject(JsonObject schemaObject, Type clrType)
    {
        var props = SchemaKeywords.GetProperties(schemaObject);
        if (props is null)
            return null; // no `properties` keyword → nothing both-present to clash on.

        var clrByJsonName = BuildClrLookup(clrType);

        foreach (var (name, propSchemaNode) in props)
        {
            // Row #24 — schema property absent from TConfig: ignored at runtime (ignore-unknown) → FINE.
            if (!clrByJsonName.TryGetValue(name, out var clrProp))
                continue;

            if (propSchemaNode is not JsonObject propSchema)
                continue; // boolean subschema declares no type → FINE (row #23-like).

            var clash = ClassifyProperty(name, propSchema, clrProp.PropertyType);
            if (clash is not null)
                return clash; // short-circuit on first clash.
        }

        return null;
    }

    /// <summary>
    /// Build the CLR name → property lookup EXACTLY as STJ resolves it under
    /// <see cref="ProcessorConfig.SerializerOptions"/>: the JSON-facing name is <c>[JsonPropertyName]</c> if
    /// present else the raw CLR member name (NO camelCase policy — Pitfall 2), matched case-insensitively
    /// (<c>PropertyNameCaseInsensitive = true</c>). Public instance properties cover positional-record
    /// synthesized properties (they have setters — confirmed by the Plan-01 spike, RESEARCH Open Q3).
    /// </summary>
    private static Dictionary<string, PropertyInfo> BuildClrLookup(Type clrType)
    {
        static string JsonName(PropertyInfo p) =>
            p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;

        var result = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // ToDictionary would throw on a duplicate JSON name; last-writer-wins is the safer choice for
            // a defensive startup check (a duplicate is an author error STJ would also reject, but Gate A
            // must never throw — D-02). Distinct keys are the overwhelming norm.
            result[JsonName(p)] = p;
        }

        return result;
    }

    /// <summary>
    /// Apply the LOCKED STJ Type-Clash Rule Table (Plan-01 spike CONFIRMED #13/#5/#8/#22 as CLASH) to a
    /// single both-present property. Returns a clash detail or null (FINE). D-02 tie-break: when genuinely
    /// ambiguous, FINE.
    /// </summary>
    private static string? ClassifyProperty(string name, JsonObject propSchema, Type clr)
    {
        // Unwrap Nullable<T> for value-type comparison, but remember the original nullability for #21/#22.
        var underlying = Nullable.GetUnderlyingType(clr);
        var effective = underlying ?? clr;
        var clrAcceptsNull = !clr.IsValueType || underlying is not null;

        var types = SchemaKeywords.GetTypes(propSchema); // null when no `type` keyword.
        var hasEnum = SchemaKeywords.HasStringEnum(propSchema, out var enumIsStringOnly);

        // A string-enum (e.g. `{"enum":["A","B"]}`) frequently omits an explicit `type`. Treat it as String
        // for classification (the enum values are JSON strings).
        if (types is null && hasEnum && enumIsStringOnly)
            types = new[] { SchemaValueType.String };

        // Row #23 — no `type` keyword (and not a string-enum): unconstrained → cannot prove a clash → FINE.
        if (types is null)
            return null;

        // Split off the `null` member (nullable union). The remaining non-null members are what must bind.
        var hasNull = types.Contains(SchemaValueType.Null);
        var nonNull = types.Where(t => t != SchemaValueType.Null).Distinct().ToArray();

        // Rows #21/#22 — a schema that admits `null` requires a CLR type that accepts null.
        if (hasNull && !clrAcceptsNull)
            return Detail(name, "null", clr); // #22 CONFIRMED CLASH (Plan-01 spike A3).

        if (nonNull.Length == 0)
            return null; // `type:"null"` only → only null is valid; clrAcceptsNull already checked → FINE.

        // Row #26 — a union of non-null members: CLASH if ANY member would fail to bind (conservative).
        foreach (var t in nonNull)
        {
            var memberClash = ClassifyScalar(name, t, propSchema, effective, clr);
            if (memberClash is not null)
                return memberClash;
        }

        return null;
    }

    /// <summary>
    /// Classify a single (non-null) schema <see cref="SchemaValueType"/> against the effective CLR type.
    /// </summary>
    private static string? ClassifyScalar(string name, SchemaValueType schemaType, JsonObject propSchema, Type effective, Type declared)
    {
        switch (schemaType)
        {
            case SchemaValueType.String:
                // Row #13 — string-enum → CLR enum: CONFIRMED CLASH (Plan-01 spike A1; no
                // JsonStringEnumConverter registered → STJ binds enums numerically, a JSON string fails).
                if (effective.IsEnum)
                    return Detail(name, "string-enum", declared);
                // Rows #7/#9/#10/#14 — string → string/Guid/DateTime/DateTimeOffset: FINE (D-02 tie-break;
                // value-level GUID/format failures are a Gate B / in-transit concern, out of D-02's scope).
                if (effective == typeof(string) || effective == typeof(Guid)
                    || effective == typeof(DateTime) || effective == typeof(DateTimeOffset)
                    || effective == typeof(DateOnly) || effective == typeof(TimeOnly)
                    || effective == typeof(TimeSpan) || effective == typeof(char))
                    return null;
                // Rows #8/#12 — string → numeric/bool/collection/object: CLASH (no AllowReadingFromString).
                return Detail(name, "string", declared);

            case SchemaValueType.Integer:
                // Rows #1/#2 — integer → integral/floating/decimal: FINE.
                if (IsNumeric(effective))
                    return null;
                // Row #3 + inverse — integer → string/bool/collection/object: CLASH.
                return Detail(name, "integer", declared);

            case SchemaValueType.Number:
                // Rows #4 — number → floating/decimal: FINE. Row #5/#6 — number → integral: CONFIRMED CLASH
                // (Plan-01 spike A2a; a schema-valid 3.14 does not bind to int).
                if (IsIntegral(effective))
                    return Detail(name, "number", declared);
                if (IsNumeric(effective))
                    return null;
                return Detail(name, "number", declared);

            case SchemaValueType.Boolean:
                // Rows #11/#12 — boolean → bool: FINE; else CLASH.
                return effective == typeof(bool) ? null : Detail(name, "boolean", declared);

            case SchemaValueType.Array:
                // Rows #16/#17 — array → collection: FINE (recurse items × element type); else CLASH.
                if (!TryGetEnumerableElementType(effective, out var elementType))
                    return Detail(name, "array", declared);
                // Recurse into the element schema (`items`) against the element type, if both are objects.
                var itemSchema = SchemaKeywords.GetItems(propSchema);
                if (itemSchema is JsonObject itemObject && elementType is not null)
                    return ClassifyNestedValue(name, itemObject, elementType);
                return null;

            case SchemaValueType.Object:
                // Rows #19/#20 — object → class/record: FINE (recurse property-by-property); else CLASH.
                if (IsBindableObject(effective))
                    return WalkObject(propSchema, effective);
                return Detail(name, "object", declared);

            default:
                return null; // SchemaValueType.Null is handled by the caller; nothing else exists.
        }
    }

    /// <summary>
    /// Classify a nested value (array element) schema against a CLR element type. Mirrors
    /// <see cref="ClassifyProperty"/> but for a value rather than a named property, recursing through the
    /// same type-clash rules so a nested clash never slips past Gate A (D-04).
    /// </summary>
    private static string? ClassifyNestedValue(string name, JsonObject valueSchema, Type clr)
    {
        var underlying = Nullable.GetUnderlyingType(clr);
        var effective = underlying ?? clr;
        var types = SchemaKeywords.GetTypes(valueSchema);
        if (SchemaKeywords.HasStringEnum(valueSchema, out var stringOnly) && types is null && stringOnly)
            types = new[] { SchemaValueType.String };
        if (types is null)
            return null;

        foreach (var t in types.Where(t => t != SchemaValueType.Null).Distinct())
        {
            var clash = ClassifyScalar(name + "[]", t, valueSchema, effective, clr);
            if (clash is not null)
                return clash;
        }

        return null;
    }

    private static string Detail(string name, string schemaType, Type clr) =>
        $"property '{name}': schema {schemaType} clashes with CLR {clr.Name}";

    private static bool IsNumeric(Type t) => IsIntegral(t) || IsFloating(t);

    private static bool IsIntegral(Type t) =>
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
        || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);

    private static bool IsFloating(Type t) =>
        t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    /// <summary>A class/record that is neither a primitive/string nor a collection — a recurse target.</summary>
    private static bool IsBindableObject(Type t) =>
        !t.IsPrimitive && !t.IsEnum
        && t != typeof(string) && t != typeof(Guid)
        && t != typeof(DateTime) && t != typeof(DateTimeOffset)
        && t != typeof(DateOnly) && t != typeof(TimeOnly) && t != typeof(TimeSpan)
        && t != typeof(decimal)
        && !TryGetEnumerableElementType(t, out _);

    /// <summary>
    /// True if <paramref name="t"/> is a collection STJ binds a JSON array into (<c>T[]</c>,
    /// <c>List&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c>, <c>IEnumerable&lt;T&gt;</c>, …). <c>string</c> is
    /// <see cref="IEnumerable"/> but is explicitly excluded.
    /// </summary>
    private static bool TryGetEnumerableElementType(Type t, out Type? elementType)
    {
        elementType = null;
        if (t == typeof(string))
            return false;

        if (t.IsArray)
        {
            elementType = t.GetElementType();
            return true;
        }

        if (t.IsGenericType)
        {
            var generic = t.GetGenericTypeDefinition();
            if (generic == typeof(List<>) || generic == typeof(IList<>)
                || generic == typeof(IReadOnlyList<>) || generic == typeof(ICollection<>)
                || generic == typeof(IReadOnlyCollection<>) || generic == typeof(IEnumerable<>)
                || generic == typeof(HashSet<>) || generic == typeof(ISet<>))
            {
                elementType = t.GetGenericArguments()[0];
                return true;
            }
        }

        // A concrete type implementing IEnumerable<T> (and not a dictionary, which STJ binds from a JSON
        // object) is array-bindable.
        var enumerableInterface = t.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerableInterface is not null && typeof(IEnumerable).IsAssignableFrom(t))
        {
            var arg = enumerableInterface.GetGenericArguments()[0];
            // Exclude dictionaries (KeyValuePair element) — those bind from a JSON object, not an array.
            if (!(arg.IsGenericType && arg.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)))
            {
                elementType = arg;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the declared JSON-Schema keywords directly off the schema's JSON tree. JsonSchema.Net 9.2.1
    /// exposes no keyword accessors on a parsed <see cref="JsonSchema"/>, and a JSON Schema is itself JSON,
    /// so the structural walk reads <c>properties</c> / <c>type</c> / <c>enum</c> / <c>items</c> from the
    /// <see cref="JsonObject"/> form of the same definition.
    /// </summary>
    private static class SchemaKeywords
    {
        /// <summary>The <c>properties</c> keyword as name → subschema node, or null when absent.</summary>
        public static IEnumerable<KeyValuePair<string, JsonNode?>>? GetProperties(JsonObject schema) =>
            schema.TryGetPropertyValue("properties", out var node) && node is JsonObject obj
                ? obj
                : null;

        /// <summary>The single-schema <c>items</c> keyword, or null when absent / array form.</summary>
        public static JsonNode? GetItems(JsonObject schema) =>
            schema.TryGetPropertyValue("items", out var node) ? node : null;

        /// <summary>
        /// The <c>type</c> keyword as a normalized array of <see cref="SchemaValueType"/>, or null when
        /// absent or unrecognized.
        /// </summary>
        public static SchemaValueType[]? GetTypes(JsonObject schema)
        {
            if (!schema.TryGetPropertyValue("type", out var node) || node is null)
                return null;

            if (node is JsonValue value && value.TryGetValue<string>(out var single))
                return TryMap(single, out var t) ? new[] { t } : null;

            if (node is JsonArray array)
            {
                var list = new List<SchemaValueType>();
                foreach (var item in array)
                    if (item is JsonValue v && v.TryGetValue<string>(out var s) && TryMap(s, out var mapped))
                        list.Add(mapped);
                return list.Count > 0 ? list.ToArray() : null;
            }

            return null;
        }

        /// <summary>
        /// True if the schema declares an <c>enum</c> keyword; <paramref name="stringOnly"/> is true when
        /// every enum member is a JSON string (the string-enum case relevant to rule #13/#14).
        /// </summary>
        public static bool HasStringEnum(JsonObject schema, out bool stringOnly)
        {
            stringOnly = false;
            if (!schema.TryGetPropertyValue("enum", out var node) || node is not JsonArray array || array.Count == 0)
                return false;

            stringOnly = array.All(e => e is JsonValue v && v.TryGetValue<string>(out _));
            return true;
        }

        private static bool TryMap(string token, out SchemaValueType type)
        {
            switch (token)
            {
                case "object": type = SchemaValueType.Object; return true;
                case "array": type = SchemaValueType.Array; return true;
                case "boolean": type = SchemaValueType.Boolean; return true;
                case "string": type = SchemaValueType.String; return true;
                case "number": type = SchemaValueType.Number; return true;
                case "integer": type = SchemaValueType.Integer; return true;
                case "null": type = SchemaValueType.Null; return true;
                default: type = default; return false;
            }
        }
    }
}
