using System.Text.Json;
using BaseApi.Core.Validation;
using FluentValidation;
using Json.Schema;

namespace BaseApi.Service.Features.Schema;

/// <summary>
/// Create-side validator. <c>Include(new BaseDtoValidator&lt;...&gt;())</c> absorbs the
/// shared Name/Version/Description rules (VALID-20). <c>Definition</c> is validated in two
/// steps: parse-as-JSON (syntactic — VALID-08 / ERROR-05 surfaces field-level errors),
/// then evaluate against <c>MetaSchemas.Draft202012</c> to confirm the user-supplied
/// payload IS itself a valid JSON Schema (semantic — VALID-08).
/// <para>
/// <b>SSRF lockdown (VALID-08 + VALID-09 / D-05 / D-06):</b> the meta-schema dialect pin
/// (draft 2020-12) and the SSRF no-op global fetcher now live in <see cref="JsonSchemaConfig"/>'s
/// static ctor — the single source of SSRF truth. It is triggered here by referencing
/// <see cref="JsonSchemaConfig.DefaultOptions"/> in the meta-schema evaluation below, which fires
/// the cctor BEFORE any evaluation runs (Pitfall 3).
/// </para>
/// </summary>
public sealed class SchemaCreateDtoValidator : AbstractValidator<SchemaCreateDto>
{
    public SchemaCreateDtoValidator()
    {
        Include(new BaseDtoValidator<SchemaCreateDto>());   // VALID-20

        RuleFor(x => x.Definition)
            .NotEmpty()
            .Custom((definition, ctx) =>
            {
                JsonDocument? doc = null;
                try
                {
                    doc = JsonDocument.Parse(definition);
                }
                catch (JsonException ex)
                {
                    ctx.AddFailure(nameof(SchemaCreateDto.Definition),
                        $"Definition is not valid JSON: {ex.Message}");
                    return;
                }
                try
                {
                    // JsonSchemaConfig.DefaultOptions reference fires the SSRF-locking cctor (D-06 / Pitfall 3).
                    var results = MetaSchemas.Draft202012.Evaluate(
                        doc.RootElement,
                        JsonSchemaConfig.DefaultOptions);
                    if (!results.IsValid)
                    {
                        // VALID-09: any $ref pointing outside the registry fails IsValid=false
                        // because the global no-op fetcher returns null -> no outbound HTTP.
                        ctx.AddFailure(nameof(SchemaCreateDto.Definition),
                            "Definition is not a valid JSON Schema (draft 2020-12).");
                    }
                }
                finally
                {
                    doc?.Dispose();
                }
            });
    }
}

/// <summary>
/// Update-side validator. Mirrors <see cref="SchemaCreateDtoValidator"/>. The SSRF lockdown
/// (dialect pin + global no-op fetcher) is owned by <see cref="JsonSchemaConfig"/>'s
/// static ctor — set once on first reference to <see cref="JsonSchemaConfig.DefaultOptions"/>
/// (triggered by the meta-schema evaluation below), which is sufficient regardless of which
/// validator runs first (D-05 / D-06).
/// </summary>
public sealed class SchemaUpdateDtoValidator : AbstractValidator<SchemaUpdateDto>
{
    public SchemaUpdateDtoValidator()
    {
        Include(new BaseDtoValidator<SchemaUpdateDto>());   // VALID-20

        RuleFor(x => x.Definition)
            .NotEmpty()
            .Custom((definition, ctx) =>
            {
                JsonDocument? doc = null;
                try
                {
                    doc = JsonDocument.Parse(definition);
                }
                catch (JsonException ex)
                {
                    ctx.AddFailure(nameof(SchemaUpdateDto.Definition),
                        $"Definition is not valid JSON: {ex.Message}");
                    return;
                }
                try
                {
                    // JsonSchemaConfig.DefaultOptions reference fires the SSRF-locking cctor (D-06 / Pitfall 3).
                    var results = MetaSchemas.Draft202012.Evaluate(
                        doc.RootElement,
                        JsonSchemaConfig.DefaultOptions);
                    if (!results.IsValid)
                    {
                        ctx.AddFailure(nameof(SchemaUpdateDto.Definition),
                            "Definition is not a valid JSON Schema (draft 2020-12).");
                    }
                }
                finally
                {
                    doc?.Dispose();
                }
            });
    }
}
