namespace Keeper.Health;

/// <summary>KEEP-03 (D-10/D-12): Stephen Toub AsyncManualResetEvent — a swappable TaskCompletionSource. Starts CLOSED (fail-safe, D-12). No polling, no volatile-flag loop.</summary>
public sealed class L2HealthGate : IL2HealthGate
{
    // D-12: start CLOSED -> a PENDING tcs (not yet completed).
    private volatile TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => _tcs.TrySetResult(true);   // idempotent set; continuations run async (D-10)

    public void Close()
    {
        var current = _tcs;
        if (!current.Task.IsCompleted)
            return;                                   // already closed — idempotent no-op
        var fresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.CompareExchange(ref _tcs, fresh, current);  // atomic vs a concurrent Open()
    }

    public Task WaitForOpenAsync(CancellationToken ct)
    {
        var openTask = _tcs.Task;                     // snapshot current state
        if (openTask.IsCompleted) return openTask;    // fast path: already open
        return openTask.WaitAsync(ct);                // net8.0 built-in; throws OCE on cancel (D-11)
    }
}
