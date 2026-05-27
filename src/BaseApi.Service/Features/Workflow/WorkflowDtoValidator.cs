using BaseApi.Core.Validation;
using Cronos;
using FluentValidation;

namespace BaseApi.Service.Features.Workflow;

/// <summary>
/// Create-side validator for <see cref="WorkflowCreateDto"/>.
/// <c>Include(new BaseDtoValidator&lt;...&gt;())</c> absorbs the shared
/// Name/Version/Description rules (VALID-20). Per-entity rules:
/// <list type="bullet">
///   <item><b>VALID-17</b> — <c>EntryStepIds</c> NotNull + Count > 0 + each unique +
///     no Guid.Empty. Non-existent (but well-formed) Step Guids surface as 23503 → 422
///     via Phase 4 mapper.</item>
///   <item><b>VALID-18</b> — <c>AssignmentIds</c> unique when present + no Guid.Empty
///     when present (null is valid — Workflow may have no assignments).</item>
///   <item><b>VALID-19</b> — <c>CronExpression</c>: when present, parses as a 5-field
///     <c>Cronos.CronExpression</c> via <see cref="BeValidStandardCron"/>. 6-field
///     expressions are rejected (SC#4). Null is valid (Workflow not scheduled per
///     ENTITY-08).</item>
/// </list>
/// </summary>
public sealed class WorkflowCreateDtoValidator : AbstractValidator<WorkflowCreateDto>
{
    public WorkflowCreateDtoValidator()
    {
        Include(new BaseDtoValidator<WorkflowCreateDto>());   // VALID-20

        // VALID-17 — EntryStepIds NotEmpty + each unique + none Guid.Empty.
        RuleFor(x => x.EntryStepIds)
            .NotNull()
            .Must(ids => ids.Count > 0)
            .WithMessage("EntryStepIds must contain at least one Step Id.")
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("EntryStepIds must be unique.")
            .Must(ids => ids.All(id => id != Guid.Empty))
            .WithMessage("EntryStepIds must not contain Guid.Empty.");

        // VALID-18 — AssignmentIds unique when present + no Guid.Empty when present.
        RuleFor(x => x.AssignmentIds)
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("AssignmentIds must be unique when present.")
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
            .WithMessage("AssignmentIds must not contain Guid.Empty.");

        // VALID-19 — CronExpression: when present, parses as 5-field Cronos Standard.
        // Null is valid (workflow not scheduled per ENTITY-08).
        RuleFor(x => x.CronExpression)
            .Must(BeValidStandardCron)
            .When(x => !string.IsNullOrWhiteSpace(x.CronExpression))
            .WithMessage("CronExpression must be a valid 5-field cron expression (e.g., '0 0 * * *').");
    }

    /// <summary>
    /// FluentValidation predicate that returns true iff the expression parses as a
    /// 5-field Cronos.CronExpression (default <c>CronFormat.Standard</c>). Catches
    /// <see cref="CronFormatException"/> (6-field expressions reject at SC#4 →
    /// HTTP 400 via the Phase 4 validation handler).
    /// </summary>
    private static bool BeValidStandardCron(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return true;
        try
        {
            CronExpression.Parse(expr);   // defaults to CronFormat.Standard (5 fields)
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// Update-side validator for <see cref="WorkflowUpdateDto"/>. Mirrors
/// <see cref="WorkflowCreateDtoValidator"/> rule-for-rule.
/// </summary>
public sealed class WorkflowUpdateDtoValidator : AbstractValidator<WorkflowUpdateDto>
{
    public WorkflowUpdateDtoValidator()
    {
        Include(new BaseDtoValidator<WorkflowUpdateDto>());   // VALID-20

        // VALID-17 — EntryStepIds NotEmpty + each unique + none Guid.Empty.
        RuleFor(x => x.EntryStepIds)
            .NotNull()
            .Must(ids => ids.Count > 0)
            .WithMessage("EntryStepIds must contain at least one Step Id.")
            .Must(ids => ids.Distinct().Count() == ids.Count)
            .WithMessage("EntryStepIds must be unique.")
            .Must(ids => ids.All(id => id != Guid.Empty))
            .WithMessage("EntryStepIds must not contain Guid.Empty.");

        // VALID-18 — AssignmentIds unique when present + no Guid.Empty when present.
        RuleFor(x => x.AssignmentIds)
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("AssignmentIds must be unique when present.")
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
            .WithMessage("AssignmentIds must not contain Guid.Empty.");

        // VALID-19 — CronExpression: when present, parses as 5-field Cronos Standard.
        RuleFor(x => x.CronExpression)
            .Must(BeValidStandardCron)
            .When(x => !string.IsNullOrWhiteSpace(x.CronExpression))
            .WithMessage("CronExpression must be a valid 5-field cron expression (e.g., '0 0 * * *').");
    }

    private static bool BeValidStandardCron(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return true;
        try
        {
            CronExpression.Parse(expr);
            return true;
        }
        catch (CronFormatException)
        {
            return false;
        }
    }
}
