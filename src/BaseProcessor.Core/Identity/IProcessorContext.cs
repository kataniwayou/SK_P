using Messaging.Contracts;

namespace BaseProcessor.Core.Identity;

/// <summary>
/// D-06: the mutable singleton holder shared between the startup orchestrator (writer of identity +
/// definitions + the Healthy flip) and the liveness heartbeat (reader of identity + definitions +
/// the Healthy gate). Populated in two startup loops:
/// <list type="number">
///   <item>Loop A resolves identity by SourceHash → <see cref="SetIdentity"/> (Id + 3 schema Ids).</item>
///   <item>Loop B resolves each non-null input/output definition → <see cref="SetDefinition"/>.</item>
/// </list>
/// then <see cref="MarkHealthy"/> flips the latch.
///
/// <para>
/// Per RESEARCH Open Q1, the holder exposes BOTH a cheap volatile <see cref="IsHealthy"/> for the
/// per-beat heartbeat gate AND a <see cref="WhenHealthy"/> Task latch for Phase 27's
/// queue-bind-after-Healthy await — costs nothing now, forward-fits EXEC-01.
/// </para>
///
/// <para>
/// <b>Memory-visibility invariant (WR-03):</b> only <see cref="IsHealthy"/>/<see cref="WhenHealthy"/>
/// carry synchronization. The identity and definition properties (<see cref="Id"/>,
/// <see cref="InputSchemaId"/>, <see cref="OutputSchemaId"/>, <see cref="ConfigSchemaId"/>,
/// <see cref="InputDefinition"/>, <see cref="OutputDefinition"/>, <see cref="Name"/>,
/// <see cref="Version"/>) are plain auto-properties with NO
/// volatile/barrier semantics. They are only safe to read from another thread AFTER observing
/// <see cref="IsHealthy"/> == <c>true</c> or after <see cref="WhenHealthy"/> has completed — the
/// full barrier in <see cref="MarkHealthy"/>'s <c>Interlocked.Exchange</c> publishes the prior
/// identity/definition writes. Reading these properties from another thread WITHOUT first observing
/// Healthy may return stale nulls.
/// </para>
/// </summary>
public interface IProcessorContext
{
    /// <summary>The resolved processor Id (null until Loop A completes).</summary>
    Guid? Id { get; }

    /// <summary>The input schema Id (null for a source processor).</summary>
    Guid? InputSchemaId { get; }

    /// <summary>The output schema Id (null for a sink processor).</summary>
    Guid? OutputSchemaId { get; }

    /// <summary>The config schema Id (carried for completeness; never resolved to a definition — D-05).</summary>
    Guid? ConfigSchemaId { get; }

    /// <summary>The resolved processor Name (DB single source of truth; null until Loop A completes). WR-03: read after IsHealthy.</summary>
    string? Name { get; }

    /// <summary>The resolved processor Version (DB single source of truth; null until Loop A completes). WR-03: read after IsHealthy.</summary>
    string? Version { get; }

    /// <summary>The resolved input schema definition (null until Loop B resolves it).</summary>
    string? InputDefinition { get; }

    /// <summary>The resolved output schema definition (null until Loop B resolves it).</summary>
    string? OutputDefinition { get; }

    /// <summary>
    /// True once identity + all required (non-null) definitions are resolved (LIVE-04 meaning of
    /// "Healthy"). Cheap volatile read for the per-beat heartbeat gate.
    /// </summary>
    bool IsHealthy { get; }

    /// <summary>
    /// Completes when <see cref="MarkHealthy"/> is first called (Phase 27 queue-bind-after-Healthy
    /// await). Continuations run asynchronously to avoid inline-completion deadlocks.
    /// </summary>
    Task WhenHealthy { get; }

    /// <summary>Stores the resolved Id + the three schema Ids from the identity response.</summary>
    void SetIdentity(ProcessorIdentityFound identity);

    /// <summary>
    /// Stores the resolved definition into <see cref="InputDefinition"/> when
    /// <paramref name="schemaId"/> matches <see cref="InputSchemaId"/>, or
    /// <see cref="OutputDefinition"/> when it matches <see cref="OutputSchemaId"/>.
    /// </summary>
    void SetDefinition(Guid schemaId, string definition);

    /// <summary>Flips the Healthy latch and completes <see cref="WhenHealthy"/> (idempotent).</summary>
    void MarkHealthy();
}
