using System.Security.Cryptography;
using System.Text;
using MassTransit;
using MassTransit.Middleware;   // Partitioner + Murmur3UnsafeHashGenerator (8.5.5 namespace — verified vs the installed assembly, RESEARCH A1/A2)
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;

namespace Keeper.Recovery;

/// <summary>
/// KEEP-09 / D-02 / D-06 — the SINGLE-OWNER endpoint config for the shared <see cref="KeeperQueues.Recovery"/>
/// ("keeper-recovery") endpoint. Mirrors the <c>FaultEntryStepDispatchConsumerDefinition</c> single-owner
/// precedent (Pitfalls 1 &amp; 4): the endpoint-level <c>UseMessageRetry</c> and the
/// five <c>UsePartitioner&lt;T&gt;</c> calls are endpoint-scoped, so ONLY this one definition may register them —
/// the other four recovery <c>ConsumerDefinition</c>s leave <c>ConfigureConsumer</c> an INTENTIONAL no-op.
/// <para>
/// The five <c>UsePartitioner&lt;T&gt;</c> calls share a SINGLE <see cref="Partitioner"/> instance keyed on the
/// <see cref="IKeeperRecoverable"/> 4-tuple (<c>corr:wf:ProcessorId:executionId</c>, EXCLUDING StepId — D-12),
/// so UPDATE/REINJECT/INJECT/DELETE/CLEANUP for the SAME exec serialize into the same partition slot (UPDATE
/// precedes that exec's CLEANUP/INJECT) while different execs run in parallel. <see cref="Partitioner"/> +
/// <see cref="Murmur3UnsafeHashGenerator"/> live in <c>MassTransit.Middleware</c> in 8.5.5; the shared-instance
/// <c>UsePartitioner&lt;T&gt;(IConsumePipeConfigurator, IPartitioner, keyProvider)</c> overload is used.
/// </para>
/// NO per-consumer ConfigureError/SetQueueArgument anywhere (Pitfall 3) — give-ups inherit the consolidated
/// skp-dlq-1 route from BaseConsole.Core's once-per-endpoint error filter.
/// </summary>
public sealed class UpdateConsumerDefinition : ConsumerDefinition<UpdateConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;
    private readonly IOptions<RecoveryOptions> _recoveryOptions;

    public UpdateConsumerDefinition(IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions)
    {
        _retryOptions = retryOptions;
        _recoveryOptions = recoveryOptions;
        EndpointName = KeeperQueues.Recovery;   // "keeper-recovery" — shared by all five recovery consumers
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<UpdateConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Endpoint-level bounded immediate retry (single source of truth = "Retry" section). The transient
        // RecoveryGateTimeoutException re-attempts here; after exhaustion it dead-letters to skp-dlq-1.
        endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));

        // One SHARED partitioner so the same 4-tuple key collides into the same slot across all five types.
        // 8.5.5's endpoint-level (IConsumePipeConfigurator) shared-IPartitioner overload keys on a Guid
        // (the string+Encoding overloads bind to the consumer pipe, not the endpoint — verified vs the
        // installed assembly, RESEARCH A1/A2). So derive a DETERMINISTIC Guid from the canonical 4-tuple
        // string: identical 4-tuple → identical PartitionKey string → identical Guid → identical slot, so
        // ordering semantics are exactly the 4-tuple's (StepId still excluded). PartitionGuid wraps
        // PartitionKey, keeping the string the single source of truth the test pins.
        // IN-02: the Murmur3 layer is REQUIRED by the API shape, not redundant decoration. PartitionGuid
        // already gives a uniform SHA256-derived Guid, but the 8.5.5 Partitioner ctor takes an IHashGenerator
        // and re-hashes the key bytes to pick a slot — so the effective slot is murmur3(guid.bytes) %
        // PartitionCount. Both hashes are deterministic; do NOT "simplify" by dropping the Murmur3 generator
        // (the ctor needs one) and do NOT drop the SHA256 Guid (the endpoint overload is Guid-keyed).
        var partition = new Partitioner(_recoveryOptions.Value.PartitionCount, new Murmur3UnsafeHashGenerator());
        endpointConfigurator.UsePartitioner<KeeperUpdate>(partition, p => PartitionGuid(p.Message));
        endpointConfigurator.UsePartitioner<KeeperReinject>(partition, p => PartitionGuid(p.Message));
        endpointConfigurator.UsePartitioner<KeeperInject>(partition, p => PartitionGuid(p.Message));
        endpointConfigurator.UsePartitioner<KeeperDelete>(partition, p => PartitionGuid(p.Message));
        endpointConfigurator.UsePartitioner<KeeperCleanup>(partition, p => PartitionGuid(p.Message));
    }

    /// <summary>KEEP-09 / D-12 — the per-key partition key is the <see cref="IKeeperRecoverable"/> 4-tuple
    /// (the composite-backup key shape), deliberately EXCLUDING StepId so all five states for one exec
    /// serialize together. <c>public static</c> (a pure key helper, no DI/state) so <c>RecoveryPartitionFacts</c>
    /// can pin the shape without InternalsVisibleTo (which would expose Keeper's top-level Program to the
    /// test assembly and collide with BaseApi.Service's Program).</summary>
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
