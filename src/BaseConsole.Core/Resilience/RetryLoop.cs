namespace BaseConsole.Core.Resilience;

/// <summary>D-08 / A3: the shared bounded retry helper. Runs <c>limit</c> IMMEDIATE attempts
/// (no backoff) and SURFACES exhaustion (returns the last exception in <see cref="RetryOutcome{T}"/>
/// rather than throwing) so the pipeline routes the correct terminal per op: read-exhaust → REINJECT,
/// write-exhaust → INJECT, delete-exhaust → DELETE, send-exhaust → re-throw (→ bus _error, D-10). One
/// place for the A3 semantics — no per-site duplication.</summary>
public static class RetryLoop
{
    public static async Task<RetryOutcome<T>> ExecuteAsync<T>(
        Func<Task<T>> op, int limit, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < Math.Max(1, limit); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return RetryOutcome<T>.Ok(await op().ConfigureAwait(false)); }
            catch (Exception ex) { last = ex; }   // immediate retry, no delay (A3)
        }
        return RetryOutcome<T>.Exhausted(last!);
    }
}

/// <summary>The result of a <see cref="RetryLoop.ExecuteAsync{T}"/>: Succeeded carries Value; an exhausted
/// loop carries the last Error so the caller can log/route. Never both.</summary>
public readonly record struct RetryOutcome<T>(bool Succeeded, T? Value, Exception? Error)
{
    public static RetryOutcome<T> Ok(T value) => new(true, value, null);
    public static RetryOutcome<T> Exhausted(Exception error) => new(false, default, error);
}
