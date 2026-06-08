using Keeper.Health;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>KEEP-06: the Keeper INJECT state — STRICT order read→write→send→delete:
/// <list type="number">
///   <item>read the composite-backup copy (absent/empty → <see cref="RecoveryDataGoneException"/>, D-04);</item>
///   <item>mint a fresh <c>entryId</c> and write the data to L2[entryId] with NO TTL (the data key is
///   TTL-free by contract);</item>
///   <item>Send a reconstructed <see cref="StepCompleted"/> (same entryId + executionId) to
///   <c>queue:{OrchestratorQueues.Result}</c> — byte-indistinguishable from a direct completion (ORCH-01);</item>
///   <item>delete the now-redundant composite copy (net-zero invariant).</item>
/// </list>
/// Each op goes through the Guard/RetryLoop re-throw path (D-04). Runs only after the gate opens (base
/// D-03).</summary>
public sealed class InjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider, IL2HealthGate gate,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions,
    IOptions<BackupOptions> backupOptions)
    : RecoveryConsumerBase<KeeperInject>(redis, sendProvider, gate, retryOptions, recoveryOptions, backupOptions)
{
    protected override async Task HandleAsync(KeeperInject m, CancellationToken ct)
    {
        var composite = L2ProjectionKeys.CompositeBackup(m.CorrelationId, m.WorkflowId, m.ProcessorId, m.ExecutionId);

        var data = await Guard(async () =>
        {
            var raw = await Db.StringGetAsync(composite);
            if (raw.IsNullOrEmpty) throw new RecoveryDataGoneException();   // D-04 terminal
            return raw.ToString();
        }, ct);

        var entryId = NewId.NextGuid();
        await Guard(() => Db.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), data), ct);   // NO TTL on the data key

        var completed = new StepCompleted(m.WorkflowId, m.StepId, m.ProcessorId)
        {
            CorrelationId = m.CorrelationId,
            ExecutionId = m.ExecutionId,
            EntryId = entryId,
        };
        var ep = await Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
        await Guard(() => ep.Send(completed, ct), ct);

        await Guard(() => Db.KeyDeleteAsync(composite), ct);   // composite now redundant — net-zero invariant
    }
}
