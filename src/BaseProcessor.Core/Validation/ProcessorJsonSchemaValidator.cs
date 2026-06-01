using System.Text.Json;
using Json.Schema;

namespace BaseProcessor.Core.Validation;

/// <summary>
/// EXEC-03/05 (D-05/D-06): the SSRF-locked Json.Schema validator PORTED into BaseProcessor.Core
/// (firewall — cannot reference BaseApi.Service). Mirrors JsonSchemaConfig's SSRF static ctor +
/// PayloadConfigSchemaValidator's parse-guard/Evaluate/flatten, but RETURNS a bool instead of
/// throwing (the WebApi version is an HTTP 422 gate; the processor version drives a business Failed).
/// </summary>
public static class ProcessorJsonSchemaValidator
{
    static ProcessorJsonSchemaValidator()
    {
        // VERBATIM from JsonSchemaConfig.cs:24-27 — pin dialect + SSRF lockdown. Both this and
        // JsonSchemaConfig set the SAME process-wide globals to IDENTICAL values (safe, Pitfall 3).
        Dialect.Default = Dialect.Draft202012;            // library default is V1, not 2020-12
        SchemaRegistry.Global.Fetch = (_, _) => null;     // SSRF — no outbound $ref fetch
    }

    /// <summary>Referencing this fires the SSRF-locking cctor BEFORE any Evaluate (Pitfall 2/3).</summary>
    public static EvaluationOptions DefaultOptions { get; } = new() { OutputFormat = OutputFormat.List };

    /// <summary>
    /// D-06: null/whitespace <paramref name="definition"/> -> true (skip validation). An unparseable
    /// definition -> false (never a crash). Otherwise evaluates <paramref name="data"/> and returns
    /// IsValid; on failure, <paramref name="errors"/> carries the flattened messages.
    /// </summary>
    public static bool TryValidate(string? definition, string data, out IReadOnlyList<string> errors)
    {
        errors = Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(definition))
            return true;   // empty definition skips validation (D-06 / Pitfall 4 — guard BEFORE FromText)

        JsonSchema schema;
        try { schema = JsonSchema.FromText(definition); }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException)
        {
            errors = new[] { "Schema definition is not valid JSON Schema." };
            return false;  // unparseable -> business Failed (D-06), never a host crash
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch (JsonException)
        {
            errors = new[] { "Data is not valid JSON." };
            return false;  // malformed data -> business Failed, never a crash
        }

        using (doc)
        {
            EvaluationResults results;
            try
            {
                // DefaultOptions reference fires the SSRF cctor (Pitfall 3) + OutputFormat.List.
                results = schema.Evaluate(doc.RootElement, DefaultOptions);
            }
            catch (JsonSchemaException)
            {
                // SSRF lockdown (T-27-01 / Pitfall 2): an external $ref CANNOT be fetched (the global
                // no-op fetcher returns null), so JsonSchema.Net 9.2.1 raises a RefResolutionException
                // (a JsonSchemaException) during Evaluate rather than reaching out to the network. D-06
                // says the consumer must NEVER crash on a bad definition — so an unresolvable $ref is a
                // business Failed (false), not a host crash. The lockdown holds: no outbound fetch occurs.
                errors = new[] { "Schema definition could not be evaluated (unresolved $ref)." };
                return false;
            }

            if (results.IsValid) return true;

            // flatten — VERBATIM shape from PayloadConfigSchemaValidator.cs:87-92
            var flat = (results.Details ?? Enumerable.Empty<EvaluationResults>())
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(kv => $"{d.InstanceLocation}: {kv.Value}"))
                .ToList();
            if (flat.Count == 0 && results.Errors is { Count: > 0 })
                flat = results.Errors.Select(kv => $"{results.InstanceLocation}: {kv.Value}").ToList();
            errors = flat;
            return false;
        }
    }
}
