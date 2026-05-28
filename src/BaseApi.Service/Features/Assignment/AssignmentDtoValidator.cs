using System.Text.Json;
using BaseApi.Core.Validation;
using FluentValidation;

namespace BaseApi.Service.Features.Assignment;

/// <summary>
/// Create-side validator. <c>Include(new BaseDtoValidator&lt;...&gt;())</c> absorbs the
/// shared Name/Version/Description rules (VALID-20). Per-entity rules:
/// <list type="bullet">
///   <item><b>VALID-15</b> — <c>StepId</c> must not be
///     <see cref="Guid.Empty"/>. Field-level 400 if violated. Non-existent (but
///     well-formed) FK Guids surface as Postgres SQLSTATE 23503 → HTTP 422 via the
///     Phase 4 PostgresExceptionMapper (constraint <c>fk_assignment_step_id</c>).</item>
///   <item><b>VALID-16</b> — <c>Payload</c> must be valid JSON syntax (validated via
///     <see cref="System.Text.Json.JsonDocument.Parse(string,System.Text.Json.JsonDocumentOptions)"/>)
///     AND at most 1,048,576 characters (~1 MB cap). The MaxLength rule fires BEFORE
///     <c>JsonDocument.Parse</c> via FluentValidation rule ordering — prevents DoS via
///     pathologically large parse-target strings.</item>
/// </list>
/// <para>
/// <b>VALID-21 deferred to v2</b> — payload-against-referenced-schema conformance is
/// now structurally impossible at this layer (Phase 10 removed the direct schema
/// reference from Assignment). Any future revival would need a new design
/// (e.g., a processor-side schema reference for payload conformance). v1 ships
/// syntactic validation only.
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

        // VALID-16 — Payload: required + max length + valid JSON syntax.
        // .Cascade(CascadeMode.Stop) makes the docstring's DoS-prevention claim load-bearing:
        // FluentValidation 12 defaults RuleLevelCascadeMode to Continue, so without Stop the
        // .Custom(JsonDocument.Parse) would run on oversized payloads anyway. Mirrors the
        // pattern in OrchestrationDtoValidator.WorkflowIdsValidator (WR-03 fix).
        RuleFor(x => x.Payload)
            .Cascade(CascadeMode.Stop)
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

        // .Cascade(CascadeMode.Stop) — see AssignmentCreateDtoValidator rationale (WR-01).
        RuleFor(x => x.Payload)
            .Cascade(CascadeMode.Stop)
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
