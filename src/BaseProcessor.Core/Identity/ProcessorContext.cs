using Messaging.Contracts;

namespace BaseProcessor.Core.Identity;

/// <summary>
/// Thread-safe backing implementation of <see cref="IProcessorContext"/> (D-06).
///
/// <para>
/// <c>public sealed</c> so <c>services.AddSingleton&lt;IProcessorContext, ProcessorContext&gt;()</c>
/// resolves across the assembly boundary without <c>InternalsVisibleTo</c> — same reason as
/// <c>BaseConsole.Core.Health.StartupGate</c>.
/// </para>
///
/// <para>
/// <see cref="IsHealthy"/> is backed by the StartupGate int-latch idiom
/// (<c>Volatile.Read</c>/<c>Interlocked.Exchange</c> — Interlocked has no bool overload in .NET 8).
/// <see cref="WhenHealthy"/> is backed by a <see cref="TaskCompletionSource"/> with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>. <see cref="MarkHealthy"/> is
/// idempotent: a second call is a no-op (the TCS is guarded by the latch CAS).
/// </para>
/// </summary>
public sealed class ProcessorContext : IProcessorContext
{
    private int _isHealthy; // 0 = false, 1 = true (Interlocked.Exchange has no bool overload in .NET 8)

    private readonly TaskCompletionSource _healthy =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc/>
    public Guid? Id { get; private set; }

    /// <inheritdoc/>
    public Guid? InputSchemaId { get; private set; }

    /// <inheritdoc/>
    public Guid? OutputSchemaId { get; private set; }

    /// <inheritdoc/>
    public Guid? ConfigSchemaId { get; private set; }

    /// <inheritdoc/>
    public string? InputDefinition { get; private set; }

    /// <inheritdoc/>
    public string? OutputDefinition { get; private set; }

    /// <inheritdoc/>
    public bool IsHealthy => Volatile.Read(ref _isHealthy) == 1;

    /// <inheritdoc/>
    public Task WhenHealthy => _healthy.Task;

    /// <inheritdoc/>
    public void SetIdentity(ProcessorIdentityFound identity)
    {
        Id = identity.Id;
        InputSchemaId = identity.InputSchemaId;
        OutputSchemaId = identity.OutputSchemaId;
        ConfigSchemaId = identity.ConfigSchemaId;
    }

    /// <inheritdoc/>
    public void SetDefinition(Guid schemaId, string definition)
    {
        if (schemaId == InputSchemaId)
            InputDefinition = definition;
        if (schemaId == OutputSchemaId)
            OutputDefinition = definition;
    }

    /// <inheritdoc/>
    public void MarkHealthy()
    {
        // CAS the latch first; only the winning caller completes the TCS (idempotent — a second
        // call is a no-op and never double-completes WhenHealthy).
        if (Interlocked.Exchange(ref _isHealthy, 1) == 0)
            _healthy.TrySetResult();
    }
}
