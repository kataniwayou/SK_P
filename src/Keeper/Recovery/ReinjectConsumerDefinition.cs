using System.Security.Cryptography;
using System.Text;
using Messaging.Contracts;

namespace Keeper.Recovery;

/// <summary>
/// D-04 / OQ-1 (Plan 02) — this type is no longer a live <c>ConsumerDefinition</c>. The keeper-recovery
/// endpoint config (the <c>UseMessageRetry</c> latch + the three shared <c>UsePartitioner&lt;T&gt;</c> calls)
/// RE-HOMED into <see cref="RecoveryEndpointBinder"/>'s <c>ConnectReceiveEndpoint</c> callback, because a
/// statically-configured MassTransit 8.5.5 endpoint cannot be runtime-paused (KEEP-04 needs a connected
/// <see cref="MassTransit.HostReceiveEndpointHandle"/> to Stop/Start). The three recovery consumers are now
/// registered with <c>ExcludeFromConfigureEndpoints()</c>, so their <c>ConsumerDefinition</c>s are no longer
/// attached — the two pure no-op siblings (<c>InjectConsumerDefinition</c>/<c>DeleteConsumerDefinition</c>)
/// were deleted, and this class survives ONLY as the home of the partition-key static helpers.
/// <para>
/// <see cref="PartitionKey"/> / <see cref="PartitionGuid"/> are <c>public static</c> pure key helpers
/// (no DI/state) pinned by <c>RecoveryPartitionFacts</c> and consumed by the binder's
/// <c>UsePartitioner&lt;T&gt;</c> key selectors. They derive a DETERMINISTIC slot from the
/// <see cref="IKeeperRecoverable"/> 4-tuple (<c>corr:wf:ProcessorId:executionId</c>, EXCLUDING StepId —
/// D-12) so REINJECT/INJECT/DELETE for the SAME exec serialize into the same partition slot while different
/// execs run in parallel. The Guid form feeds the 8.5.5 Guid-keyed endpoint partitioner overload.
/// </para>
/// </summary>
public static class ReinjectConsumerDefinition
{
    /// <summary>KEEP-09 / D-12 — the canonical per-key partition string: the <see cref="IKeeperRecoverable"/>
    /// 4-tuple, deliberately EXCLUDING StepId so all three states for one exec serialize together.
    /// <c>public static</c> (a pure key helper, no DI/state) so <c>RecoveryPartitionFacts</c> can pin the
    /// shape without InternalsVisibleTo (which would expose Keeper's top-level Program to the test assembly
    /// and collide with BaseApi.Service's Program).</summary>
    public static string PartitionKey(IKeeperRecoverable m) =>
        $"{m.CorrelationId:D}:{m.WorkflowId:D}:{m.ProcessorId:D}:{m.ExecutionId:D}";

    /// <summary>Deterministic Guid over the canonical <see cref="PartitionKey"/> string for the 8.5.5
    /// Guid-keyed endpoint partitioner overload. Same 4-tuple → same Guid → same partition slot; StepId
    /// excluded by construction (it is never part of <see cref="PartitionKey"/>).</summary>
    public static Guid PartitionGuid(IKeeperRecoverable m)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(PartitionKey(m)));
        return new Guid(hash.AsSpan(0, 16));   // first 128 bits — stable across processes
    }
}
