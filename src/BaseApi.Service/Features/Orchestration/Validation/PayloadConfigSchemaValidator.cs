using System.Text.Json;
using BaseApi.Service.Features.Schema;
using Json.Schema;

namespace BaseApi.Service.Features.Orchestration.Validation;

/// <summary>
/// Payload↔ConfigSchema conformance gate (D-10 / L1-VALIDATE-06 + L1-VALIDATE-08). Iterates every
/// Assignment in the L1 snapshot, resolves <c>StepId → ProcessorId → ConfigSchemaId → Schema.Definition</c>,
/// parses each Definition into a <see cref="JsonSchema"/> via a per-Start LOCAL
/// <c>Dictionary&lt;Guid, JsonSchema&gt;</c> cache keyed by Schema.Id (each schema parsed at most once —
/// L1-VALIDATE-08), and evaluates the Assignment's <c>Payload</c> against it.
/// <para>
/// A <c>null</c> <c>Processor.ConfigSchemaId</c> passes (no schema to validate against). On a conformance
/// failure the flattened JsonSchema.Net messages (per D-03) are surfaced via
/// <see cref="OrchestrationValidationException.PayloadConfigSchema"/> → HTTP 422.
/// </para>
/// <para>
/// <b>SSRF lockdown (T-14-09):</b> evaluation uses <see cref="JsonSchemaConfig.DefaultOptions"/> — the
/// single SSRF source of truth whose static ctor pins the dialect and installs the global no-op fetcher
/// (no outbound <c>$ref</c> fetch). The parse cache is declared as a LOCAL inside <see cref="Validate"/>,
/// never an instance field — the seam is DI-Scoped, so an instance field would leak parsed schemas across
/// requests (T-14-10).
/// </para>
/// </summary>
internal sealed class PayloadConfigSchemaValidator
{
    public void Validate(WorkflowGraphSnapshot snapshot)
    {
        // Per-Start LOCAL cache (D-10 / L1-VALIDATE-08): each Schema.Id parsed at most once.
        // MUST stay a local — an instance field would leak parsed schemas across DI-Scoped requests.
        var schemaCache = new Dictionary<Guid, JsonSchema>();

        foreach (var assignment in snapshot.Assignments.Values)
        {
            // Defensive resolution — a missing step/processor is the cycle/missing-step gate's concern
            // (those ran first); skip rather than throw a spurious payload error here.
            if (!snapshot.Steps.TryGetValue(assignment.StepId, out var step)) continue;
            if (!snapshot.Processors.TryGetValue(step.ProcessorId, out var proc)) continue;

            var cfgId = proc.ConfigSchemaId;
            if (cfgId is null) continue;   // null ConfigSchemaId passes — no schema to validate against.

            if (!schemaCache.TryGetValue(cfgId.Value, out var schema))
            {
                if (!snapshot.Schemas.TryGetValue(cfgId.Value, out var schemaDto)) continue; // defensive
                // A persisted Definition normally passed the create-time meta-schema gate
                // (SchemaCreateDtoValidator), so it should parse. But that invariant is enforced
                // elsewhere; a Definition written by a future bypassing code path, or a row mutated
                // directly in the DB, would otherwise throw a raw Json.Schema/JsonException here — which
                // is NOT an OrchestrationValidationException, so the domain handler bails and it falls
                // through to FallbackExceptionHandler → HTTP 500. Translate it to the gate's 422 instead.
                try
                {
                    schema = JsonSchema.FromText(schemaDto.Definition);
                }
                catch (Exception ex) when (ex is JsonException or JsonSchemaException)
                {
                    throw OrchestrationValidationException.PayloadConfigSchema(
                        assignment.Id,
                        new[] { $"Config schema '{cfgId.Value}' is not a valid JSON Schema." });
                }
                schemaCache[cfgId.Value] = schema;
            }

            JsonDocument? payloadDoc = null;
            try
            {
                // A malformed Assignment.Payload (e.g. empty string or non-JSON) would otherwise throw a
                // raw JsonException — not an OrchestrationValidationException — and land as HTTP 500. A
                // malformed payload is conceptually a payload-conformance failure, so translate it to the
                // gate's 422 envelope instead, matching the create-side SchemaDtoValidator parse-guard.
                try
                {
                    payloadDoc = JsonDocument.Parse(assignment.Payload);
                }
                catch (JsonException)
                {
                    throw OrchestrationValidationException.PayloadConfigSchema(
                        assignment.Id, new[] { "Payload is not valid JSON." });
                }
                // JsonSchemaConfig.DefaultOptions reference fires the SSRF-locking cctor (Pitfall 3) and
                // supplies OutputFormat.List so results.Details is the flat node list (T-14-09).
                var results = schema.Evaluate(payloadDoc.RootElement, JsonSchemaConfig.DefaultOptions);
                if (!results.IsValid)
                {
                    var errorStrings = (results.Details ?? Enumerable.Empty<EvaluationResults>())
                        .Where(d => d.Errors is { Count: > 0 })
                        .SelectMany(d => d.Errors!.Select(kv => $"{d.InstanceLocation}: {kv.Value}"))
                        .ToList();
                    if (errorStrings.Count == 0 && results.Errors is { Count: > 0 })
                        errorStrings = results.Errors.Select(kv => $"{results.InstanceLocation}: {kv.Value}").ToList();
                    throw OrchestrationValidationException.PayloadConfigSchema(assignment.Id, errorStrings);
                }
            }
            finally
            {
                payloadDoc?.Dispose();
            }
        }
    }
}
