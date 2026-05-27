using BaseApi.Core.Validation;
using FluentValidation;

namespace BaseApi.Service.Features.Processor;

/// <summary>
/// Create-side validator. <c>Include(new BaseDtoValidator&lt;...&gt;())</c> absorbs the
/// shared Name/Version/Description rules (VALID-20). Per-entity rules:
/// <list type="bullet">
///   <item><b>VALID-10</b> — <c>SourceHash</c> must match lowercase SHA-256 hex regex
///     <c>^[a-f0-9]{64}$</c>. Field-level 400 if malformed; the unique-index check at
///     Postgres (<c>uq_processor_source_hash</c>) handles the duplicate-hash → 409 path.</item>
///   <item><b>VALID-11</b> — <c>InputSchemaId</c> / <c>OutputSchemaId</c> are nullable;
///     null is valid (source/sink processors per ENTITY-04). When present, must not equal
///     <c>Guid.Empty</c>. Pattern: <c>When(x =&gt; x.Field.HasValue, () =&gt; RuleFor(x =&gt; x.Field!.Value).NotEqual(Guid.Empty))</c>.</item>
/// </list>
/// Non-existent (but well-formed) FK Guids are NOT rejected here — they surface through
/// Postgres FK constraint violation (SQLSTATE 23503) and are mapped to HTTP 422 by Phase 4
/// <c>PostgresExceptionMapper</c> with the constraint field name (<c>fk_processor_input_schema_id</c>
/// → <c>input_schema_id</c>).
/// </summary>
public sealed class ProcessorCreateDtoValidator : AbstractValidator<ProcessorCreateDto>
{
    public ProcessorCreateDtoValidator()
    {
        Include(new BaseDtoValidator<ProcessorCreateDto>());   // VALID-20

        // VALID-10 — lowercase SHA-256 hex
        RuleFor(x => x.SourceHash)
            .NotEmpty()
            .Matches(@"^[a-f0-9]{64}$")
            .WithMessage("SourceHash must be a lowercase SHA-256 hex string (64 chars, [a-f0-9]).");

        // VALID-11 — InputSchemaId nullable; when present, must not be Guid.Empty.
        When(x => x.InputSchemaId.HasValue, () =>
        {
            RuleFor(x => x.InputSchemaId!.Value)
                .NotEqual(Guid.Empty)
                .WithMessage("InputSchemaId must not be Guid.Empty when provided.");
        });

        When(x => x.OutputSchemaId.HasValue, () =>
        {
            RuleFor(x => x.OutputSchemaId!.Value)
                .NotEqual(Guid.Empty)
                .WithMessage("OutputSchemaId must not be Guid.Empty when provided.");
        });
    }
}

/// <summary>
/// Update-side validator. Mirrors <see cref="ProcessorCreateDtoValidator"/> rule-for-rule.
/// SourceHash CAN change on update (the unique-index check still applies at the DB).
/// </summary>
public sealed class ProcessorUpdateDtoValidator : AbstractValidator<ProcessorUpdateDto>
{
    public ProcessorUpdateDtoValidator()
    {
        Include(new BaseDtoValidator<ProcessorUpdateDto>());   // VALID-20

        RuleFor(x => x.SourceHash)
            .NotEmpty()
            .Matches(@"^[a-f0-9]{64}$")
            .WithMessage("SourceHash must be a lowercase SHA-256 hex string (64 chars, [a-f0-9]).");

        When(x => x.InputSchemaId.HasValue, () =>
        {
            RuleFor(x => x.InputSchemaId!.Value)
                .NotEqual(Guid.Empty)
                .WithMessage("InputSchemaId must not be Guid.Empty when provided.");
        });

        When(x => x.OutputSchemaId.HasValue, () =>
        {
            RuleFor(x => x.OutputSchemaId!.Value)
                .NotEqual(Guid.Empty)
                .WithMessage("OutputSchemaId must not be Guid.Empty when provided.");
        });
    }
}
