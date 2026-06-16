using System.Diagnostics.Metrics;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orchestrator.Configuration;
using Orchestrator.Dispatch;
using Orchestrator.Observability;
using StackExchange.Redis;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Shared kit for the Phase-71 <see cref="Orchestrator.Recovery.OrchestratorResultPipeline"/> Forward/Recovery
/// facts — the orchestrator-side analog of <c>DispatchTestKit</c>. Provides a family of NSubstitute
/// <see cref="IConnectionMultiplexer"/>/<see cref="IDatabase"/> fakes covering the gate / atomic-Lua-forward /
/// recovery / cleanup fault surfaces, a <see cref="CapturingSendProvider"/> recording EVERY boxed send
/// (<see cref="IStepResult"/>, <see cref="IKeeperRecoverable"/>, <see cref="EntryStepDispatch"/>) generically,
/// the <see cref="RetryOptions"/>/<see cref="OrchestratorRecoveryOptions"/> option helpers, a real
/// <see cref="StepAdvancement"/> + <see cref="OrchestratorMetrics"/>, and slot-map/result builders.
/// <para>
/// The pipeline's only divergence from the processor is the heterogeneous slot encoding (a JSON tuple per
/// index slot) and the copy-an-existing-key data leg — so the recovery muxes seed an HGETALL whose slot
/// VALUE is the <see cref="SlotTuple"/> JSON (not a bare entryId), keyed by the tuple's <c>newEntryId</c>.
/// </para>
/// </summary>
internal static class OrchestratorPipelineTestKit
{
    // ---- options / collaborators ----

    /// <summary>The retry budget the pipeline consumes (Limit immediate attempts per L2 op + per send).</summary>
    public static IOptions<RetryOptions> Retry(int limit = 3) =>
        Options.Create(new RetryOptions { Limit = limit });

    /// <summary>The orchestrator data-TTL source (the orchestrator's equivalent of the processor's
    /// ExecutionDataTtl) — drives the copied data key's PX and the index whole-hash PEXPIRE.</summary>
    public static IOptions<OrchestratorRecoveryOptions> Recovery(int executionDataTtlSeconds = 300) =>
        Options.Create(new OrchestratorRecoveryOptions { ExecutionDataTtlSeconds = executionDataTtlSeconds });

    /// <summary>A real <see cref="StepAdvancement"/> (pure int-match helper — no I/O, safe to construct).</summary>
    public static StepAdvancement Advancement() => new();

    /// <summary>A real <see cref="OrchestratorMetrics"/> for the hermetic facts — built from a live
    /// <see cref="IMeterFactory"/>; no collector is wired so the increments are no-ops in-test.</summary>
    public static OrchestratorMetrics Metrics()
    {
        var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        return new OrchestratorMetrics(meterFactory);
    }

    // ---- inbound result + L1 step-map builders ----

    /// <summary>An inbound <see cref="StepCompleted"/> result carrying the given origin <paramref name="entryId"/>
    /// (the real L2 data key the FORWARD copies FROM / the cleanup tail deletes).</summary>
    public static StepCompleted Completed(Guid entryId, Guid? correlationId = null, Guid? executionId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = correlationId ?? Guid.NewGuid(),
            ExecutionId   = executionId ?? Guid.NewGuid(),
            EntryId       = entryId,
        };

    /// <summary>The completed step's L1 projection. <paramref name="nextStepIds"/> are the successors the
    /// FORWARD pass iterates via <see cref="StepAdvancement.SelectNext"/>.</summary>
    public static StepProjection CompletedStep(IEnumerable<Guid> nextStepIds) =>
        new(EntryCondition: 0, ProcessorId: Guid.NewGuid(), Payload: "{}", NextStepIds: nextStepIds.ToList());

    /// <summary>An L1 step map. Each (id, processorId, payload) entry becomes a next step whose
    /// <c>EntryCondition</c> = 1 (Completed) so it matches a <see cref="StepOutcome.Completed"/> outcome.</summary>
    public static IReadOnlyDictionary<Guid, StepProjection> Steps(
        params (Guid Id, Guid ProcessorId, string Payload)[] next) =>
        next.ToDictionary(
            n => n.Id,
            n => new StepProjection(EntryCondition: 1, ProcessorId: n.ProcessorId, Payload: n.Payload, NextStepIds: new List<Guid>()));

    /// <summary>The D-02 heterogeneous slot tuple the index HASH value carries (and RECOVERY parses).</summary>
    public sealed record SlotTuple(Guid NextStepId, Guid NextProcessorId, string Payload, Guid NewEntryId);

    /// <summary>
    /// Builds the <c>HashEntry[]</c> a recovery HGETALL returns: each slot's VALUE is the JSON-serialized
    /// <see cref="SlotTuple"/>, ordinals assigned in array order (0,1,2,…). A retired (Guid.Empty) entry
    /// models an already-retired inert slot.
    /// </summary>
    public static HashEntry[] Slots(params RedisValue[] slotValues)
        => slotValues.Select((v, i) => new HashEntry(i, v)).ToArray();

    /// <summary>A JSON slot value carrying the tuple's <c>newEntryId</c> (the data key RECOVERY checks).</summary>
    public static RedisValue SlotJson(Guid newEntryId, Guid? nextStepId = null, Guid? nextProcessorId = null, string payload = "{}")
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            nextStepId = nextStepId ?? Guid.NewGuid(),
            nextProcessorId = nextProcessorId ?? Guid.NewGuid(),
            payload,
            newEntryId,
        });

    /// <summary>A retired slot sentinel value (Guid.Empty.ToString()).</summary>
    public static RedisValue RetiredSlot => Guid.Empty.ToString();

    // ---- FORWARD muxes (gate exist=false → forward branch) ----

    /// <summary>FORWARD-happy mux: <c>KeyExistsAsync(MessageIndex)</c> FALSE → forward branch; the single
    /// atomic <c>ScriptEvaluateAsync</c> forward write and the cleanup tail DEL succeed. The mock <c>db</c>
    /// is returned so a fact can inspect the script call's KEYS/ARGV and the tail DEL.</summary>
    public static IConnectionMultiplexer ForwardOkL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);   // NOT exist → forward
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create(1));
        db.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);
        db.KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        return WrapMux(db);
    }

    /// <summary>Atomic-write-fault mux: existence FALSE (forward); the single atomic <c>ScriptEvaluateAsync</c>
    /// THROWS → retry exhausts → ONE OrchestratorInject. Default-stub the binding to success first, then layer
    /// the throw (so an unstubbed Task false-green is impossible). The retire/tail ops succeed.</summary>
    public static IConnectionMultiplexer AtomicWriteFaultL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: Lua atomic write unreachable");
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create(1));
        db.When(x => x.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
        db.When(x => x.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>()))
            .Do(_ => throw boom);
        db.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);
        return WrapMux(db);
    }

    /// <summary>Gate-exhaust mux: <c>KeyExistsAsync</c> THROWS for every key → the gate retry exhausts →
    /// ONE OrchestratorReinject and NO cleanup. The mock <c>db</c> is returned so a fact can assert
    /// <c>DidNotReceive().KeyDeleteAsync(...)</c>.</summary>
    public static IConnectionMultiplexer GateFaultL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: gate exist unreachable"));
        return WrapMux(db);
    }

    // ---- RECOVERY muxes (gate exist=true → recovery branch) ----

    /// <summary>
    /// RECOVERY mux: <c>KeyExistsAsync(MessageIndex)</c> TRUE (→ recovery branch); <c>HashGetAllAsync</c>
    /// returns <paramref name="slots"/>; each per-slot <c>KeyExistsAsync(ExecutionData(newEntryId))</c>
    /// resolves the <paramref name="newEntryExists"/> matrix (true=exists→re-send, false=clean not-exist→drop);
    /// any newEntryId in <paramref name="faultEntries"/> THROWS on its exist check (→ L2 fault → leaves slot).
    /// The retire/TTL/source-delete ops all succeed.
    /// </summary>
    public static IConnectionMultiplexer RecoveryL2(
        Guid messageId, HashEntry[] slots,
        IReadOnlyDictionary<Guid, bool> newEntryExists, IReadOnlyCollection<Guid> faultEntries, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        var msgKey = L2ProjectionKeys.MessageIndex(messageId);
        var existMatrix = newEntryExists.ToDictionary(kv => L2ProjectionKeys.ExecutionData(kv.Key), kv => kv.Value);

        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                if (key == msgKey) return true;                                  // recovery branch
                return existMatrix.TryGetValue(key, out var present) && present;  // exists / clean not-exist
            });
        foreach (var faultId in faultEntries)
        {
            var faultKey = L2ProjectionKeys.ExecutionData(faultId);
            var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: per-slot exist unreachable");
            db.When(x => x.KeyExistsAsync(faultKey, Arg.Any<CommandFlags>())).Do(_ => throw boom);
            db.When(x => x.KeyExistsAsync(faultKey)).Do(_ => throw boom);
        }

        db.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(slots);
        db.HashGetAllAsync(Arg.Any<RedisKey>()).Returns(slots);
        db.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);
        db.KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        return WrapMux(db);
    }

    /// <summary>RECOVERY HGETALL-fault mux: <c>KeyExistsAsync(MessageIndex)</c> TRUE (→ recovery branch), but
    /// <c>HashGetAllAsync</c> THROWS → retry exhausts → OrchestratorReinject (no source delete).</summary>
    public static IConnectionMultiplexer RecoveryHGetAllFaultL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // recovery branch
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: HGETALL unreachable");
        db.When(x => x.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())).Do(_ => throw boom);
        db.When(x => x.HashGetAllAsync(Arg.Any<RedisKey>())).Do(_ => throw boom);
        return WrapMux(db);
    }

    private static IConnectionMultiplexer WrapMux(IDatabase db)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// An <see cref="ISendEndpointProvider"/> whose every resolved endpoint records each boxed message it is
    /// asked to <c>Send</c>, generically: an <see cref="IStepResult"/> lands in <see cref="Sent"/>
    /// (orchestrator results — incl. a re-sent <see cref="StepCompleted"/>), an <see cref="IKeeperRecoverable"/>
    /// (OrchestratorInject / OrchestratorReinject / KeeperDelete) in <see cref="SentKeeper"/>, and an
    /// <see cref="EntryStepDispatch"/> in <see cref="SentDispatch"/> paired with the queue URI it went to.
    /// </summary>
    public sealed class CapturingSendProvider : ISendEndpointProvider
    {
        public List<IStepResult> Sent { get; } = new();
        public List<IKeeperRecoverable> SentKeeper { get; } = new();
        public List<(Uri Uri, EntryStepDispatch Dispatch)> SentDispatch { get; } = new();

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var o = ci[0];
                    switch (o)
                    {
                        case IStepResult sr:            Sent.Add(sr); break;
                        case IKeeperRecoverable kr:     SentKeeper.Add(kr); break;
                        case EntryStepDispatch d:       SentDispatch.Add((address, d)); break;
                    }
                    return Task.CompletedTask;
                });
            return Task.FromResult(endpoint);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }

    /// <summary>An <see cref="ISendEndpointProvider"/> whose <c>IStepResult</c> sends THROW (the send-fail case
    /// for the SLOT-03 send-before-retire fact) but whose keeper/dispatch sends still record.</summary>
    public sealed class ResultSendFailProvider : ISendEndpointProvider
    {
        public List<IKeeperRecoverable> SentKeeper { get; } = new();

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var o = ci[0];
                    if (o is IStepResult)
                        throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: result send unreachable");
                    if (o is IKeeperRecoverable kr) SentKeeper.Add(kr);
                    return Task.CompletedTask;
                });
            return Task.FromResult(endpoint);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }
}
