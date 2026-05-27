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
/// <b>Static ctor (VALID-08 + VALID-09):</b> sets <c>Dialect.Default = Dialect.Draft202012</c>
/// (RESEARCH §JsonSchema.Net "Default dialect (CRITICAL)" — library default is V1, not 2020-12),
/// and explicitly assigns <c>SchemaRegistry.Global.Fetch = (_, _) =&gt; null</c> as defense-in-depth
/// against SSRF via external <c>$ref</c> tokens. The library default is already a no-op fetcher;
/// the explicit assignment encodes the security intent at the source-tree level so a future
/// dependency upgrade that changes the default would surface here, not in production.
/// </para>
/// </summary>
public sealed class SchemaCreateDtoValidator : AbstractValidator<SchemaCreateDto>
{
    static SchemaCreateDtoValidator()
    {
        // VALID-08 — set the meta-schema dialect explicitly (default is V1, not 2020-12).
        Dialect.Default = Dialect.Draft202012;
        // VALID-09 — defense-in-depth: explicit no-op even though library default is already (_,_) => null.
        SchemaRegistry.Global.Fetch = (_, _) => null;
    }

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
                    var results = MetaSchemas.Draft202012.Evaluate(
                        doc.RootElement,
                        new EvaluationOptions { OutputFormat = OutputFormat.List });
                    if (!results.IsValid)
                    {
                        // VALID-09: any $ref pointing outside the registry fails IsValid=false
                        // because SchemaRegistry.Global.Fetch returns null -> no outbound HTTP.
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
/// Update-side validator. Mirrors <see cref="SchemaCreateDtoValidator"/> but does NOT
/// re-run the static ctor (per-type static state — Dialect.Default + SchemaRegistry.Global.Fetch
/// are set once on first touch of EITHER validator type, which is sufficient because both
/// participate in the same DI scan).
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
                    var results = MetaSchemas.Draft202012.Evaluate(
                        doc.RootElement,
                        new EvaluationOptions { OutputFormat = OutputFormat.List });
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
