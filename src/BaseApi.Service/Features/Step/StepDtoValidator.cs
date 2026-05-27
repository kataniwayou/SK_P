using BaseApi.Core.Validation;
using FluentValidation;

namespace BaseApi.Service.Features.Step;

/// <summary>
/// Create-side validator. <c>Include(new BaseDtoValidator&lt;...&gt;())</c> absorbs the
/// shared Name/Version/Description rules (VALID-20). Per-entity rules:
/// <list type="bullet">
///   <item><b>VALID-12</b> — <c>ProcessorId</c> NotEqual Guid.Empty. Non-existent (but
///     well-formed) Processor Guids surface as 23503 → 422 via Phase 4 mapper.</item>
///   <item><b>VALID-13</b> — the next-step collection: each Guid is unique within the
///     list AND none are Guid.Empty. The "not equal to own Id" sub-clause is enforced
///     at the service layer (validator does not have the path-id in context).</item>
///   <item><b>VALID-14</b> — <c>EntryCondition</c> IsInEnum.</item>
/// </list>
/// </summary>
public sealed class StepCreateDtoValidator : AbstractValidator<StepCreateDto>
{
    public StepCreateDtoValidator()
    {
        Include(new BaseDtoValidator<StepCreateDto>());   // VALID-20

        // VALID-12 — ProcessorId NotEmpty Guid.
        RuleFor(x => x.ProcessorId)
            .NotEqual(Guid.Empty)
            .WithMessage("ProcessorId must not be Guid.Empty.");

        // VALID-13 — next-step collection: each unique + none Guid.Empty.
        RuleFor(x => x.NextStepIds)
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("NextStepIds must be unique.")
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
            .WithMessage("NextStepIds must not contain Guid.Empty.");

        // VALID-14 — EntryCondition IsInEnum.
        RuleFor(x => x.EntryCondition)
            .IsInEnum();
    }
}

/// <summary>
/// Update-side validator. Mirrors <see cref="StepCreateDtoValidator"/> rule-for-rule.
/// <para>
/// <b>VALID-13 v1 limitation:</b> the "on Update, no entry equals the Step's own Id"
/// sub-clause cannot be enforced here — the validator does not have access to the path
/// <c>{id}</c>. This is documented as a v1 limitation; the service layer is the
/// canonical place to enforce it if/when the rule is tightened. v1 ships with the
/// uniqueness + non-empty checks only.
/// </para>
/// </summary>
public sealed class StepUpdateDtoValidator : AbstractValidator<StepUpdateDto>
{
    public StepUpdateDtoValidator()
    {
        Include(new BaseDtoValidator<StepUpdateDto>());   // VALID-20

        RuleFor(x => x.ProcessorId)
            .NotEqual(Guid.Empty)
            .WithMessage("ProcessorId must not be Guid.Empty.");

        RuleFor(x => x.NextStepIds)
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("NextStepIds must be unique.")
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
            .WithMessage("NextStepIds must not contain Guid.Empty.");

        RuleFor(x => x.EntryCondition)
            .IsInEnum();
    }
}
