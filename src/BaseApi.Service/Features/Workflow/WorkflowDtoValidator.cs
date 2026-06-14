using BaseApi.Core.Validation;
using Cronos;
using FluentValidation;
using Messaging.Contracts.Projections;

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

        // VALID-19 / CRON-02 — CronExpression: when present, resolves 5- or 6-field via the
        // shared CronFieldForm detector then one guarded parse. Null is valid (workflow not
        // scheduled per ENTITY-08).
        RuleFor(x => x.CronExpression)
            .Must(BeValidStandardCron)
            .When(x => !string.IsNullOrWhiteSpace(x.CronExpression))
            .WithMessage("CronExpression must be a valid 5- or 6-field cron expression (e.g., '0 0 * * *' or '*/30 * * * * *').");
    }

    /// <summary>
    /// FluentValidation predicate. Null/blank is valid (ENTITY-08). Otherwise the shared
    /// <see cref="CronFieldForm"/> detector rejects any non-5/6 field count up front WITHOUT
    /// throwing (D-02, no exception-as-control-flow), then resolves the format (6 →
    /// <c>CronFormat.IncludeSeconds</c>, 5 → <c>CronFormat.Standard</c>) for ONE guarded
    /// <see cref="CronExpression.Parse(string, CronFormat)"/>; a genuinely-malformed 5/6-token
    /// expression still rejects via <see cref="CronFormatException"/> (CRON-02).
    /// </summary>
    private static bool BeValidStandardCron(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return true;          // null/blank is valid (ENTITY-08)
        if (!CronFieldForm.IsValidFieldCount(expr)) return false;  // reject non-5/6 up front — no exception (D-02)
        var format = CronFieldForm.IsSecondsForm(expr) ? CronFormat.IncludeSeconds : CronFormat.Standard;
        try { CronExpression.Parse(expr, format); return true; }
        catch (CronFormatException) { return false; }              // genuinely-malformed 5/6-token still rejected
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

        // VALID-19 / CRON-02 — CronExpression: when present, resolves 5- or 6-field via the
        // shared CronFieldForm detector then one guarded parse.
        RuleFor(x => x.CronExpression)
            .Must(BeValidStandardCron)
            .When(x => !string.IsNullOrWhiteSpace(x.CronExpression))
            .WithMessage("CronExpression must be a valid 5- or 6-field cron expression (e.g., '0 0 * * *' or '*/30 * * * * *').");
    }

    /// <summary>
    /// Mirror of <see cref="WorkflowCreateDtoValidator.BeValidStandardCron"/> — byte-identical
    /// behavior (Pitfall 3): null/blank valid (ENTITY-08), non-5/6 rejected up front via the
    /// shared <see cref="CronFieldForm"/> detector (D-02), then ONE guarded parse with the
    /// resolved <see cref="CronFormat"/> (6 → IncludeSeconds, 5 → Standard).
    /// </summary>
    private static bool BeValidStandardCron(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return true;          // null/blank is valid (ENTITY-08)
        if (!CronFieldForm.IsValidFieldCount(expr)) return false;  // reject non-5/6 up front — no exception (D-02)
        var format = CronFieldForm.IsSecondsForm(expr) ? CronFormat.IncludeSeconds : CronFormat.Standard;
        try { CronExpression.Parse(expr, format); return true; }
        catch (CronFormatException) { return false; }              // genuinely-malformed 5/6-token still rejected
    }
}
