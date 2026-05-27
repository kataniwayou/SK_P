using System.Text.Json;
using BaseApi.Core.Validation;
using FluentValidation;

namespace BaseApi.Service.Features.Assignment;

/// <summary>
/// Create-side validator. <c>Include(new BaseDtoValidator&lt;...&gt;())</c> absorbs the
/// shared Name/Version/Description rules (VALID-20). Per-entity rules:
/// <list type="bullet">
///   <item><b>VALID-15</b> — <c>StepId</c> / <c>SchemaId</c> must not be
///     <see cref="Guid.Empty"/>. Field-level 400 if violated. Non-existent (but
///     well-formed) FK Guids surface as Postgres SQLSTATE 23503 → HTTP 422 via the
///     Phase 4 PostgresExceptionMapper.</item>
///   <item><b>VALID-16</b> — <c>Payload</c> must be valid JSON syntax (validated via
///     <see cref="System.Text.Json.JsonDocument.Parse(string,System.Text.Json.JsonDocumentOptions)"/>)
///     AND at most 1,048,576 characters (~1 MB cap). The MaxLength rule fires BEFORE
///     <c>JsonDocument.Parse</c> via FluentValidation rule ordering — prevents DoS via
///     pathologically large parse-target strings.</item>
/// </list>
/// <para>
/// <b>VALID-21 deferred to v2</b> per 08-CONTEXT line 23 — payload-against-referenced-Schema
/// conformance is NOT checked at the validator (no DB roundtrip to fetch the Schema
/// definition). v1 ships syntactic validation only; semantic schema conformance is a
/// future-phase concern.
/// </para>
/// </summary>
public sealed class AssignmentCreateDtoValidator : AbstractValidator<AssignmentCreateDto>
{
    private const int MaxPayloadBytes = 1_048_576; // 1 MB cap per VALID-16

    public AssignmentCreateDtoValidator()
    {
        Include(new BaseDtoValidator<AssignmentCreateDto>());   // VALID-20

        // VALID-15 — StepId must not be Guid.Empty.
        RuleFor(x => x.StepId)
            .NotEqual(Guid.Empty)
            .WithMessage("StepId must not be Guid.Empty.");

        // VALID-15 — SchemaId must not be Guid.Empty.
        RuleFor(x => x.SchemaId)
            .NotEqual(Guid.Empty)
            .WithMessage("SchemaId must not be Guid.Empty.");

        // VALID-16 — Payload: required + max length + valid JSON syntax.
        RuleFor(x => x.Payload)
            .NotEmpty()
            .MaximumLength(MaxPayloadBytes)
            .WithMessage($"Payload must be at most {MaxPayloadBytes} characters.")
            .Custom((payload, ctx) =>
            {
                if (string.IsNullOrEmpty(payload)) return;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    // Parsed successfully — no further conformance check (VALID-21 deferred to v2).
                }
                catch (JsonException ex)
                {
                    ctx.AddFailure(nameof(AssignmentCreateDto.Payload),
                        $"Payload is not valid JSON: {ex.Message}");
                }
            });
    }
}

/// <summary>
/// Update-side validator. Mirrors <see cref="AssignmentCreateDtoValidator"/> rule-for-rule.
/// </summary>
public sealed class AssignmentUpdateDtoValidator : AbstractValidator<AssignmentUpdateDto>
{
    private const int MaxPayloadBytes = 1_048_576; // 1 MB cap per VALID-16

    public AssignmentUpdateDtoValidator()
    {
        Include(new BaseDtoValidator<AssignmentUpdateDto>());   // VALID-20

        RuleFor(x => x.StepId)
            .NotEqual(Guid.Empty)
            .WithMessage("StepId must not be Guid.Empty.");

        RuleFor(x => x.SchemaId)
            .NotEqual(Guid.Empty)
            .WithMessage("SchemaId must not be Guid.Empty.");

        RuleFor(x => x.Payload)
            .NotEmpty()
            .MaximumLength(MaxPayloadBytes)
            .WithMessage($"Payload must be at most {MaxPayloadBytes} characters.")
            .Custom((payload, ctx) =>
            {
                if (string.IsNullOrEmpty(payload)) return;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                }
                catch (JsonException ex)
                {
                    ctx.AddFailure(nameof(AssignmentUpdateDto.Payload),
                        $"Payload is not valid JSON: {ex.Message}");
                }
            });
    }
}
