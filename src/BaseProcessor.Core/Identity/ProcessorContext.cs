using Messaging.Contracts;

namespace BaseProcessor.Core.Identity;

/// <summary>
/// Backing implementation of <see cref="IProcessorContext"/> (D-06). Only <see cref="IsHealthy"/>/
/// <see cref="WhenHealthy"/> carry synchronization; the identity/definition properties are plain
/// auto-properties safe to read cross-thread only after Healthy is observed (see the memory-visibility
/// invariant on <see cref="IProcessorContext"/>, WR-03).
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
    public string? Name { get; private set; }

    /// <inheritdoc/>
    public string? Version { get; private set; }

    /// <inheritdoc/>
    public string? InputDefinition { get; private set; }

    /// <inheritdoc/>
    public string? OutputDefinition { get; private set; }

    /// <inheritdoc/>
    public string? ConfigDefinition { get; private set; }

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
        Name = identity.Name;
        Version = identity.Version;
    }

    /// <inheritdoc/>
    public void SetDefinition(Guid schemaId, string definition)
    {
        if (schemaId == InputSchemaId)
            InputDefinition = definition;
        if (schemaId == OutputSchemaId)
            OutputDefinition = definition;
        // D-12/D-14: route the config schema id to ConfigDefinition (Gate A's input). Independent `if`
        // (not else-if) — if two roles share an Id, one fetch populates both slots (idempotent).
        if (schemaId == ConfigSchemaId)
            ConfigDefinition = definition;
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
