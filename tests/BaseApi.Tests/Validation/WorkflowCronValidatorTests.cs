using BaseApi.Service.Features.Workflow;
using Xunit;

namespace BaseApi.Tests.Validation;

/// <summary>
/// CRON-02 / D-09: pins that BOTH <see cref="WorkflowCreateDtoValidator"/> and
/// <see cref="WorkflowUpdateDtoValidator"/> (byte-identical <c>BeValidStandardCron</c>, Pitfall 3)
/// accept the 5-field standard form (regression guard, SC#3) AND the 6-field seconds form
/// (new capability, SC#2), while still rejecting malformed + wrong-field-count crons.
/// Baseline DTO is otherwise VALID (strict-SemVer Version + one non-empty EntryStepIds) so the
/// CronExpression rule is the only failing variable. Pure FluentValidation unit test — no DI/HTTP.
/// </summary>
[Trait("Phase", "63")]
public sealed class WorkflowCronValidatorTests
{
    private static WorkflowCreateDto CreateDto(string? cron) =>
        new("wf", "1.0.0", null, new() { Guid.NewGuid() }, null, cron);

    private static WorkflowUpdateDto UpdateDto(string? cron) =>
        new("wf", "1.0.0", null, new() { Guid.NewGuid() }, null, cron);

    // ---------- Accept: 5-field (SC#3 regression) + 6-field (SC#2 new capability) ----------

    [Theory]
    [InlineData("0 0 * * *")]        // 5-field standard — regression guard (SC#3)
    [InlineData("*/30 * * * * *")]   // 6-field seconds — new capability (SC#2)
    public void Create_Accepts_FiveAndSixField(string cron)
    {
        var result = new WorkflowCreateDtoValidator().Validate(CreateDto(cron));
        Assert.DoesNotContain(result.Errors, e => e.PropertyName == "CronExpression");
    }

    [Theory]
    [InlineData("0 0 * * *")]
    [InlineData("*/30 * * * * *")]
    public void Update_Accepts_FiveAndSixField(string cron)
    {
        var result = new WorkflowUpdateDtoValidator().Validate(UpdateDto(cron));
        Assert.DoesNotContain(result.Errors, e => e.PropertyName == "CronExpression");
    }

    // ---------- Reject: malformed + wrong field count ----------

    [Theory]
    [InlineData("not a cron")]       // malformed
    [InlineData("* * *")]            // 3-token wrong field count
    public void Create_Rejects_MalformedOrWrongCount(string cron)
    {
        var result = new WorkflowCreateDtoValidator().Validate(CreateDto(cron));
        Assert.Contains(result.Errors, e => e.PropertyName == "CronExpression");
    }

    [Theory]
    [InlineData("not a cron")]
    [InlineData("* * *")]
    public void Update_Rejects_MalformedOrWrongCount(string cron)
    {
        var result = new WorkflowUpdateDtoValidator().Validate(UpdateDto(cron));
        Assert.Contains(result.Errors, e => e.PropertyName == "CronExpression");
    }
}
