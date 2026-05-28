using FluentValidation;

namespace BaseApi.Service.Features.Orchestration;

/// <summary>
/// Validates the bare <c>List&lt;Guid&gt;</c> body posted to
/// <c>POST /api/v1/orchestration/start</c> and <c>/api/v1/orchestration/stop</c>
/// (Phase 9 REQ-5). Rules (CONTEXT D-08):
/// <list type="number">
///   <item>NotNull + NotEmpty — reject <c>null</c> body or empty array.</item>
///   <item>No duplicates — each Guid must appear exactly once.</item>
///   <item>No <see cref="Guid.Empty"/> entries.</item>
/// </list>
/// Auto-discovered by <c>AddBaseApiValidation</c>'s <c>AddValidatorsFromAssembly</c>
/// scan over <c>typeof(Program).Assembly</c> (Phase 6 VALID-02). No manual DI
/// registration is needed in <c>AddOrchestrationFeature</c>.
/// <para>
/// <b>One-of-a-kind validator:</b> all 5 other validators in this codebase target
/// specific DTO records (e.g., <c>WorkflowCreateDto</c>). This validator targets
/// <c>IReadOnlyList&lt;Guid&gt;</c> directly because the Orchestration request body
/// is a bare JSON array of GUIDs — no envelope DTO (SPEC.md Constraint). CONTEXT
/// D-09 acknowledges this as intentional while Start and Stop share behavior. If
/// they diverge in a future phase, refactor to typed request records.
/// </para>
/// <para>
/// Phase 4 <c>ValidationExceptionHandler</c> maps the
/// <see cref="FluentValidation.ValidationException"/> thrown by
/// <c>ValidateAndThrowAsync</c> to HTTP 400 <c>ValidationProblemDetails</c>.
/// </para>
/// </summary>
public sealed class WorkflowIdsValidator : AbstractValidator<IReadOnlyList<Guid>>
{
    public WorkflowIdsValidator()
    {
        RuleFor(ids => ids).NotNull().NotEmpty();

        RuleFor(ids => ids)
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("WorkflowIds must be unique.");

        RuleForEach(ids => ids)
            .NotEqual(Guid.Empty)
            .WithMessage("WorkflowIds must not contain Guid.Empty.");
    }
}
