using Messaging.Contracts.Projections;
using Xunit;

namespace BaseApi.Tests.Contracts;

/// <summary>
/// D-10: pins the ONE shared cron field-count → format rule that both the Orchestrator scheduler
/// (CronInterval) and the BaseApi.Service validators consume (D-03/D-04/D-05). 6 tokens → seconds
/// form; 5 tokens → standard form; any other count → invalid. Whitespace-robust (leading/trailing/
/// multiple spaces collapse). Pure string logic — no Cronos, no DI/HTTP (mirrors L2ProjectionKeysTests).
/// </summary>
[Trait("Phase", "63")]
public sealed class CronFieldFormTests
{
    [Theory]
    [InlineData("*/30 * * * * *")]   // 6-token seconds form
    [InlineData(" */30   *  *   * * * ")] // whitespace robustness → still 6 tokens
    public void IsSecondsForm_True_For_SixToken(string expr) =>
        Assert.True(CronFieldForm.IsSecondsForm(expr));

    [Theory]
    [InlineData("0 0 * * *")]        // 5-token standard form
    [InlineData("*/5 * * * *")]
    public void IsSecondsForm_False_For_FiveToken(string expr) =>
        Assert.False(CronFieldForm.IsSecondsForm(expr));

    [Theory]
    [InlineData("0 0 * * *")]
    [InlineData("*/30 * * * * *")]
    public void IsValidFieldCount_True_For_Five_Or_Six(string expr) =>
        Assert.True(CronFieldForm.IsValidFieldCount(expr));

    [Theory]
    [InlineData("* * *")]            // 4-or-fewer tokens rejected
    [InlineData("")]
    public void IsValidFieldCount_False_For_Other_Counts(string expr) =>
        Assert.False(CronFieldForm.IsValidFieldCount(expr));
}
