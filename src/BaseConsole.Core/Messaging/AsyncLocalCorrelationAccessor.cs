namespace BaseConsole.Core.Messaging;

/// <summary>
/// <see cref="AsyncLocal{T}"/>-backed <see cref="ICorrelationAccessor"/>. The ambient value
/// flows down the async call chain of a single consume operation, so the inbound filter's set
/// is visible to the handler and the outbound filter without explicit threading. Registered as
/// a Singleton in Plan 02's messaging extension (the storage is per-async-context, not
/// per-instance).
/// </summary>
public sealed class AsyncLocalCorrelationAccessor : ICorrelationAccessor
{
    private static readonly AsyncLocal<string?> _current = new();

    public string? Get() => _current.Value;

    public void Set(string? value) => _current.Value = value;
}
