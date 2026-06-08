using System;
using System.Threading;
using System.Threading.Tasks;
using Keeper.Health;
using Xunit;

namespace BaseApi.Tests.Keeper.Health;

// KEEP-03 (45-01) — the asymmetric L2 health gate (Stephen Toub swappable-TCS AsyncManualResetEvent):
//   - starts CLOSED; WaitForOpenAsync blocks (returns a not-yet-completed task) until Open() is called
//   - Open() completes any pending waiter
//   - Close() after Open() re-blocks subsequent waiters
//   - WaitForOpenAsync honors cancellation (OperationCanceledException / its TaskCanceledException subclass)
//   - Open()/Close() are idempotent (repeat calls are no-ops, no throw)
public sealed class L2HealthGateTests
{
    [Fact]
    public void Gate_Starts_Closed_WaitForOpenAsync_Blocks_Until_Open()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new L2HealthGate();

        // CLOSED start: the wait does NOT complete synchronously.
        var wait = gate.WaitForOpenAsync(ct);
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task Open_Completes_The_Wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new L2HealthGate();

        var wait = gate.WaitForOpenAsync(ct);
        Assert.False(wait.IsCompleted);

        gate.Open();

        // The prior wait now resolves (RunContinuationsAsynchronously → may complete on the pool).
        await wait.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Close_After_Open_Re_Blocks()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new L2HealthGate();

        gate.Open();
        // While open, the wait is already complete (fast path).
        await gate.WaitForOpenAsync(ct);

        gate.Close();

        // After Close() a fresh wait blocks again.
        var reblocked = gate.WaitForOpenAsync(ct);
        Assert.False(reblocked.IsCompleted);

        // And re-Open() resolves it.
        gate.Open();
        await reblocked.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(reblocked.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitForOpenAsync_Throws_OperationCanceledException_On_Cancel()
    {
        var gate = new L2HealthGate();   // CLOSED — the wait will block, then cancel.
        using var cts = new CancellationTokenSource();

        var wait = gate.WaitForOpenAsync(cts.Token);
        cts.Cancel();

        // OperationCanceledException (TaskCanceledException is a subclass — accept either).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task Open_Is_Idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new L2HealthGate();

        gate.Open();
        gate.Open();   // second Open is a no-op, no throw.

        // Still open.
        await gate.WaitForOpenAsync(ct);
        Assert.True(gate.WaitForOpenAsync(ct).IsCompleted);
    }

    [Fact]
    public void Close_Is_Idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new L2HealthGate();   // already CLOSED.

        gate.Close();   // Close on an already-closed gate is a no-op, no throw.
        gate.Close();

        // Still closed.
        Assert.False(gate.WaitForOpenAsync(ct).IsCompleted);
    }
}
