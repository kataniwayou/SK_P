using BaseProcessor.Core.Resilience;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// RESIL-01 (D-08 / A3): self-contained Wave-0 facts for the standalone <see cref="RetryLoop"/> helper.
/// Drives <see cref="RetryLoop.ExecuteAsync{T}"/> directly with a local async lambda that increments a
/// captured attempt counter. Proves: exact attempt count on exhaustion (no throw, surfaced via
/// <see cref="RetryOutcome{T}"/>), first-success short-circuit, second-attempt recovery, the
/// Math.Max(1, limit) at-least-once guard, and the Plan-02 send-exhaust re-throw contract (D-10).
/// Depends ONLY on the Task-2 RetryLoop (it already exists) — these pass NOW, not RED.
/// </summary>
public sealed class RetryLoopFacts
{
    [Fact]
    public async Task RetryLoop_Exhausts_RunsExactlyLimitAttempts_ThenSurfaces()
    {
        var ct = TestContext.Current.CancellationToken;
        var attempts = 0;

        var outcome = await RetryLoop.ExecuteAsync<string>(() =>
        {
            attempts++;
            throw new InvalidOperationException("always fails");
        }, limit: 3, ct);

        Assert.Equal(3, attempts);                       // ran exactly `limit` immediate attempts
        Assert.False(outcome.Succeeded);                 // exhaustion is SURFACED, not thrown
        Assert.IsType<InvalidOperationException>(outcome.Error);
        Assert.Null(outcome.Value);
    }

    [Fact]
    public async Task RetryLoop_Succeeds_ReturnsValue_OnFirstSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        var attempts = 0;

        var outcome = await RetryLoop.ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult("ok");
        }, limit: 3, ct);

        Assert.Equal(1, attempts);                       // no extra attempts after success
        Assert.True(outcome.Succeeded);
        Assert.Equal("ok", outcome.Value);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public async Task RetryLoop_Succeeds_OnSecondAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var attempts = 0;

        var outcome = await RetryLoop.ExecuteAsync(() =>
        {
            attempts++;
            if (attempts == 1) throw new InvalidOperationException("first attempt fails");
            return Task.FromResult("recovered");
        }, limit: 3, ct);

        Assert.Equal(2, attempts);                       // threw once, succeeded on the second
        Assert.True(outcome.Succeeded);
        Assert.Equal("recovered", outcome.Value);
    }

    [Fact]
    public async Task RetryLoop_ZeroLimit_RunsAtLeastOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var attempts = 0;

        var outcome = await RetryLoop.ExecuteAsync(() =>
        {
            attempts++;
            return Task.FromResult("once");
        }, limit: 0, ct);                                // Math.Max(1, limit) guard

        Assert.Equal(1, attempts);
        Assert.True(outcome.Succeeded);
        Assert.Equal("once", outcome.Value);
    }

    [Fact]
    public async Task SendExhaust_Propagates_WhenCallerRethrows()
    {
        var ct = TestContext.Current.CancellationToken;

        // Simulate the Plan-02 send pattern: on exhaustion the caller re-throws outcome.Error!
        // so MassTransit UseMessageRetry dead-letters to _error (D-10).
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var outcome = await RetryLoop.ExecuteAsync<string>(
                () => throw new InvalidOperationException("send failed"), limit: 2, ct);

            if (!outcome.Succeeded) throw outcome.Error!;
        });
    }
}
