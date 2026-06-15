using System.Diagnostics.Metrics;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Shared kit for the Phase-44 <see cref="ProcessorPipeline"/> Pre/In/Post/end-delete facts:
/// a configurable fake <see cref="BaseProcessorBase"/> (the In-Process seam) returning a
/// <see cref="List{ProcessItem}"/> or throwing, a <see cref="CapturingSendProvider"/> capturing BOTH
/// <see cref="IStepResult"/> (orchestrator results) AND <see cref="IKeeperRecoverable"/> (Keeper-state)
/// sends, the <see cref="RetryOptions"/>/<see cref="ProcessorLivenessOptions"/> option helpers, and a
/// family of Redis-multiplexer fakes covering the Pre/Post/end-delete fault surfaces:
/// <see cref="PresentReadWriteFaultL2"/> (output-write fault), <see cref="AbsentReadL2"/> (A2 absent
/// key), and <see cref="ReadOkDeleteFaultL2"/> (end-delete-exhaust fault).
/// </summary>
internal static class DispatchTestKit
{
    /// <summary>A trivial field-less author config for the fake — <c>{"cfg":1}</c> deserializes harmlessly
    /// into it under the framework's ignore-unknown options, keeping every pipeline-double fact deser-inert.</summary>
    public sealed record DummyConfig : ProcessorConfig;

    /// <summary>
    /// A test-double <see cref="BaseProcessor{DummyConfig}"/> whose typed In-Process <c>ProcessAsync</c> either
    /// returns a configurable <see cref="List{ProcessItem}"/> (recording the validatedData it was called with)
    /// or throws (the throw ctor serves BOTH the <c>ProcessStatusException</c> family AND the
    /// unexpected-exception case). The framework deserializes the dispatch payload into a <see cref="DummyConfig"/>
    /// before invoking the seam — the double is deserialize-inert (no field to populate).
    /// </summary>
    public sealed class FakeProcessor : BaseProcessor<DummyConfig>
    {
        private readonly Func<string, DummyConfig?, Guid, CancellationToken, Task<List<ProcessItem>>> _impl;

        public FakeProcessor(List<ProcessItem> items)
            => _impl = (validatedData, config, executionId, _) =>
            {
                Invoked = true;
                LastInputData = validatedData;
                LastExecutionId = executionId;
                return Task.FromResult(items);
            };

        public FakeProcessor(Exception toThrow)
            => _impl = (validatedData, config, executionId, _) =>
            {
                Invoked = true;
                LastInputData = validatedData;
                LastExecutionId = executionId;
                throw toThrow;
            };

        /// <summary>True once the transform was actually invoked (proves the Pre guards short-circuited or not).</summary>
        public bool Invoked { get; private set; }
        public string? LastInputData { get; private set; }
        /// <summary>The inbound per-instance executionId the seam threaded in (Guid.Empty == entry/seed).</summary>
        public Guid LastExecutionId { get; private set; }

        protected override Task<List<ProcessItem>> ProcessAsync(
            string validatedData, DummyConfig? config, Guid executionId, CancellationToken ct)
            => _impl(validatedData, config, executionId, ct);
    }

    /// <summary>Returns N completed <see cref="ProcessItem"/>s carrying the given output strings, each with
    /// a freshly minted (author-side) ExecutionId.</summary>
    public static List<ProcessItem> Items(params string[] outputs)
        => outputs.Select(o => new ProcessItem(ProcessOutcome.Completed, o, Guid.NewGuid())).ToList();

    /// <summary>Returns the given <see cref="ProcessItem"/>s verbatim (mixed Completed/Failed cases).</summary>
    public static List<ProcessItem> Items(params ProcessItem[] items) => items.ToList();

    /// <summary>
    /// A Redis multiplexer whose <c>StringGetAsync</c> resolves registered keys (PresentL2 shape) and
    /// whose <c>StringSetAsync</c> throws <see cref="RedisConnectionException"/> — the infra
    /// output-write-fault case (Post → KeeperInject). <c>KeyDeleteAsync</c> is a no-op success (the
    /// end-delete on this path succeeds).
    /// </summary>
    public static IConnectionMultiplexer PresentReadWriteFaultL2(
        IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return values.TryGetValue(key, out var v) ? (RedisValue)v : RedisValue.Null;
            });
        // Throw on the output WRITE regardless of which StringSetAsync overload the compiler binds
        // (SE.Redis 2.13.1 carries a keepTtl 6-arg AND When 4/5-arg overloads). Use When/Do per
        // overload (no ForAnyArgs cross-overload reset) so the infra-throw stub is robust.
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: Redis write unreachable");
        db.When(x => x.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
#pragma warning disable CS0618 // the When-overloads are obsolete but still bindable — cover them too
        db.When(x => x.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<When>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
        db.When(x => x.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>()))
            .Do(_ => throw boom);
#pragma warning restore CS0618
        // SE.Redis 2.13 added the Expiration/ValueCondition overload (TimeSpan implicitly converts to
        // Expiration) — the `expiry:`-named call can bind here, so cover it too.
        db.When(x => x.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(),
                Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
        // KeyDeleteAsync is a no-op success (the source-delete tail runs and succeeds on this path).
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);   // A19: array DEL count removed
        // Phase-51 FWD-01: KeyExists(L2[messageId]) FALSE → forward branch (the slot HASH ops succeed so the
        // data-write fault is reached). HashSetAsync + KeyExpireAsync stubbed OK so the allocation index lands.
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        db.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>()).Returns(true);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// A Redis multiplexer whose <c>StringGetAsync</c> resolves registered keys, and whose
    /// <c>StringSetAsync</c> (output write) AND <c>KeyDeleteAsync</c> (source-delete tail) both SUCCEED — the
    /// happy / business-fail Post + source-delete paths. The write + delete are explicitly stubbed (so a
    /// <c>Received()</c> assertion can prove the TTL'd write and the source-delete invocation). Phase-51:
    /// <c>KeyExists(L2[messageId])</c> returns FALSE (forward branch) and the slot HASH ops succeed.
    /// </summary>
    public static IConnectionMultiplexer PresentReadWriteDeleteOkL2(
        IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return values.TryGetValue(key, out var v) ? (RedisValue)v : RedisValue.Null;
            });
        db.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);   // A19: array DEL count removed (value ignored by code)
        db.KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // A19: best-effort persist resolves
        // Phase-51 FWD-01: KeyExists FALSE → forward branch; slot HASH ops succeed (allocation lands).
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        db.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>()).Returns(true);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// FWD-01 forward-happy mux: existence-check FALSE → forward branch; the atomic forward-Post write
    /// (<c>ScriptEvaluateAsync</c> — index HSET + whole-hash PEXPIRE + data SET-with-PX in ONE server-side op)
    /// and the source delete all succeed. The whole forward happy path runs to the source-delete tail. The
    /// mock <c>db</c> is returned so a fact can inspect the single <c>ScriptEvaluateAsync</c> call's KEYS/ARGV
    /// and the tail <c>KeyDeleteAsync</c>. (The legacy HashSet/KeyExpire/StringSet stubs are retained — they are
    /// harmless, never called now that the forward write is one script.)
    /// </summary>
    public static IConnectionMultiplexer ForwardOkL2(
        IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => values.TryGetValue(((RedisKey)ci[0]).ToString(), out var v) ? (RedisValue)v : RedisValue.Null);
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);   // FWD-01: NOT exist L2[messageId]
        // The atomic forward-Post write is one Lua ScriptEvaluateAsync → stub it to a success result.
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create(1));   // atomic forward write succeeds
        // Legacy sub-op stubs retained (harmless — never called now the forward write is one script).
        db.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>()).Returns(true);
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);   // A19: array DEL count removed (value ignored by code)
        db.KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // A19: best-effort persist resolves
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// NODROP-01 / INFRA-01 atomic-write-fault mux (merges the two former forward slot- and data-write fault
    /// muxes into one): existence FALSE (forward); the SINGLE atomic forward-Post write
    /// (<c>ScriptEvaluateAsync</c>) THROWS → the retry loop exhausts → the single atomic-write exhaust routes to
    /// ONE keeper <c>INJECT</c> (data in-hand) — NO drop path remains (spec §10). The default binding is stubbed
    /// to a success result first, then a <c>When/Do</c> throw is layered on top (so an unstubbed Task false-green
    /// is impossible). KeyDelete OK so the tail still runs.
    /// </summary>
    public static IConnectionMultiplexer AtomicWriteFaultL2(
        IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => values.TryGetValue(((RedisKey)ci[0]).ToString(), out var v) ? (RedisValue)v : RedisValue.Null);
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: Lua atomic write unreachable");
        // Default-stub the binding first, then layer the throw (mirrors the multi-overload guard style).
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create(1));
        db.When(x => x.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
        // Guard the trailing-arg overload (no explicit CommandFlags) the compiler may also bind.
        db.When(x => x.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>()))
            .Do(_ => throw boom);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);   // A19: array DEL count removed
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// FWD-03 forward source-delete-fault mux: existence FALSE; allocation HSET + KeyExpire + data SET all
    /// succeed; the tail <c>KeyDeleteAsync</c> THROWS → exhausts → keeper <c>DELETE</c>.
    /// </summary>
    public static IConnectionMultiplexer ForwardDeleteFaultL2(
        IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => values.TryGetValue(((RedisKey)ci[0]).ToString(), out var v) ? (RedisValue)v : RedisValue.Null);
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        db.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>()).Returns(true);
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: Redis delete unreachable");
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>()))
            .Do(_ => throw boom);
        // A19 Pitfall-1 guard: the production tail now calls the ARRAY DEL overload — it MUST throw too,
        // or an unstubbed Task<long>→0L false-greens the exhaust branch.
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())).Do(_ => throw boom);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// A Redis multiplexer whose <c>StringGetAsync</c> THROWS <see cref="RedisConnectionException"/> for
    /// every key (the Pre read-fault path) — the read loop exhausts → <c>KeeperReinject</c>. The mock db is
    /// returned so a fact can assert <c>db.DidNotReceive().KeyDeleteAsync(...)</c> (end-delete skipped).
    /// </summary>
    public static IConnectionMultiplexer ReadFaultL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<RedisValue>>(_ => throw new RedisConnectionException(
                ConnectionFailureType.UnableToConnect, "stub: Redis read unreachable"));
        // Phase-51 FWD-01: KeyExists FALSE → forward branch (so the Pre read-fault REINJECT is reached).
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// A Redis multiplexer whose <c>StringGetAsync</c> returns <see cref="RedisValue.Null"/> for EVERY key
    /// (the A2 absent/empty Pre-read path) — the read closure's <c>IsNullOrEmpty</c> guard throws
    /// <c>KeyAbsentException</c>, the loop exhausts, and the Pre routes to <c>KeeperReinject</c>.
    /// <c>KeyDeleteAsync</c> is a no-op success (so a DidNotReceive assertion proves end-delete was skipped).
    /// </summary>
    public static IConnectionMultiplexer AbsentReadL2(out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(_ => RedisValue.Null);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        // Phase-51 FWD-01: KeyExists FALSE → forward branch (so the Pre read-exhaust REINJECT is reached).
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// A Redis multiplexer whose <c>StringGetAsync</c> resolves registered keys, <c>StringSetAsync</c>
    /// (output write) succeeds, but <c>KeyDeleteAsync</c> (the source-delete tail) throws
    /// <see cref="RedisConnectionException"/> — the end-delete-exhaust path (finally → KeeperDelete).
    /// Mirrors the overload-robust When/Do style on the two <c>KeyDeleteAsync</c> overloads.
    /// </summary>
    public static IConnectionMultiplexer ReadOkDeleteFaultL2(
        IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return values.TryGetValue(key, out var v) ? (RedisValue)v : RedisValue.Null;
            });
        db.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(true);
        // Phase-51 FWD-01: KeyExists FALSE → forward branch; slot HASH ops succeed so the source-delete tail
        // is reached (and faults).
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        db.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>()).Returns(true);
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: Redis delete unreachable");
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>()))
            .Do(_ => throw boom);
        // A19 Pitfall-1 guard: the production tail now calls the ARRAY DEL overload — it MUST throw too,
        // or an unstubbed Task<long>→0L false-greens the exhaust branch.
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())).Do(_ => throw boom);
        // A19: best-effort persist SUCCEEDS here — only the DEL exhausts (AC-5 persist-then-escalate path).
        db.KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// A19 persist-exhaust fault mux (sibling of <see cref="ReadOkDeleteFaultL2"/>): <c>StringGetAsync</c>
    /// resolves registered keys, <c>StringSetAsync</c> (output write) succeeds, the forward slot HASH ops
    /// succeed, but BOTH the tail array <c>KeyDeleteAsync(RedisKey[], …)</c> AND the best-effort
    /// <c>KeyPersistAsync</c> THROW <see cref="RedisConnectionException"/> — the D-03 persist-exhaust path
    /// (the production code must STILL send the KeeperDelete despite the failed persist fall-through). Backs
    /// the new <c>EndDelete_PersistExhaust_StillSendsKeeper</c> fact (Plan 04).
    /// </summary>
    public static IConnectionMultiplexer ReadOkDeleteAndPersistFaultL2(
        IReadOnlyDictionary<string, string> values, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return values.TryGetValue(key, out var v) ? (RedisValue)v : RedisValue.Null;
            });
        db.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(true);
        // Phase-51 FWD-01: KeyExists FALSE → forward branch; slot HASH ops succeed so the source-delete tail
        // is reached (and faults).
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        db.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>()).Returns(true);
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: Redis delete unreachable");
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>()))
            .Do(_ => throw boom);
        // A19 Pitfall-1 guard: the array DEL overload throws (the production tail calls it).
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())).Do(_ => throw boom);
        // A19: persist ALSO throws here — BOTH the array DEL and KeyPersistAsync exhaust (D-03 fall-through).
        db.When(x => x.KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())).Do(_ => throw boom);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    // ---- Phase-51 RECOVERY muxes (existence-check TRUE → recovery branch) ----

    /// <summary>
    /// Builds the <c>HashEntry[]</c> a recovery HGETALL returns: <c>new HashEntry(slotInt, entryId.ToString("D"))</c>
    /// for each given entryId, slot ordinals assigned in array order (0,1,2,…). A <see cref="Guid.Empty"/>
    /// entry models an already-retired (inert) slot.
    /// </summary>
    public static HashEntry[] Slots(params Guid[] entryIds)
        => entryIds.Select((id, i) => new HashEntry(i, id.ToString("D"))).ToArray();

    /// <summary>
    /// RECOV-02/03 recovery mux: <c>KeyExistsAsync(MessageIndex)</c> returns TRUE (→ the recovery branch);
    /// <c>HashGetAllAsync(MessageIndex)</c> returns <paramref name="slots"/>; each per-entry
    /// <c>KeyExistsAsync(ExecutionData(entryId))</c> resolves the <paramref name="entryExists"/> matrix
    /// (<c>true</c>=exists→completed, <c>false</c>=clean not-exist→drop); any entryId in
    /// <paramref name="faultEntries"/> THROWS on its exist check (→ <c>infra_entryId</c>, leaves the slot).
    /// The retire <c>HashSetAsync</c>, the TTL <c>KeyExpireAsync</c>, and the source <c>KeyDeleteAsync</c> all
    /// succeed (so a fact can assert <c>Received</c>/<c>DidNotReceive</c> on each).
    /// </summary>
    public static IConnectionMultiplexer RecoveryL2(
        Guid messageId, HashEntry[] slots,
        IReadOnlyDictionary<Guid, bool> entryExists, IReadOnlyCollection<Guid> faultEntries, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        var msgKey = L2ProjectionKeys.MessageIndex(messageId);
        var existMatrix = entryExists.ToDictionary(kv => L2ProjectionKeys.ExecutionData(kv.Key), kv => kv.Value);

        // KeyExistsAsync resolves per key: the MessageIndex (dispatcher recovery branch) is TRUE; each
        // ExecutionData(entryId) maps to the matrix (clean true/false). Fault entries are overridden below.
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                if (key == msgKey) return true;                                  // recovery branch
                return existMatrix.TryGetValue(key, out var present) && present; // exists / clean not-exist
            });
        foreach (var faultId in faultEntries)
        {
            var faultKey = L2ProjectionKeys.ExecutionData(faultId);
            var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: per-entry exist unreachable");
            db.When(x => x.KeyExistsAsync(faultKey, Arg.Any<CommandFlags>())).Do(_ => throw boom);
            db.When(x => x.KeyExistsAsync(faultKey)).Do(_ => throw boom);
        }

        db.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(slots);
        db.HashGetAllAsync(Arg.Any<RedisKey>()).Returns(slots);
        // Retire (slot → Guid.Empty), the D-06 TTL refresh, and the source delete all succeed.
        db.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);   // A19: array DEL count removed (value ignored by code)
        db.KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // A19: best-effort persist resolves
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// RECOV-01 recovery HGETALL-fault mux: <c>KeyExistsAsync(MessageIndex)</c> TRUE (→ recovery branch), but
    /// <c>HashGetAllAsync(MessageIndex)</c> THROWS → the retry loop exhausts → <c>KeeperReinject</c> (no source
    /// delete). The mock <c>db</c> is returned so a fact can assert <c>DidNotReceive().KeyDeleteAsync(...)</c>.
    /// </summary>
    public static IConnectionMultiplexer RecoveryHGetAllFaultL2(Guid messageId, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // recovery branch
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: HGETALL unreachable");
        db.When(x => x.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())).Do(_ => throw boom);
        db.When(x => x.HashGetAllAsync(Arg.Any<RedisKey>())).Do(_ => throw boom);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// SLOT-03 send-fail recovery mux: <c>KeyExistsAsync(MessageIndex)</c> TRUE; HGETALL returns
    /// <paramref name="slots"/>; every per-entry exist check is TRUE (→ completed). The retire
    /// <c>HashSetAsync</c> is stubbed but a fact asserting <c>DidNotReceive()</c> proves the send-before-retire
    /// invariant when the SendResult throws (the send fails via a throwing send provider, so the retire is
    /// never reached). The mock <c>db</c> is returned for that assertion.
    /// </summary>
    public static IConnectionMultiplexer RecoveryAllCompletedL2(Guid messageId, HashEntry[] slots, out IDatabase db)
    {
        db = Substitute.For<IDatabase>();
        // recovery branch (MessageIndex exists) AND every per-entry data key exists → every slot is completed.
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(slots);
        db.HashGetAllAsync(Arg.Any<RedisKey>()).Returns(slots);
        db.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);   // A19: array DEL count removed (value ignored by code)
        db.KeyPersistAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);   // A19: best-effort persist resolves
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// An <see cref="ISendEndpointProvider"/> whose <c>IStepResult</c> sends THROW (the send-fail case for the
    /// SLOT-03 send-before-retire fact) but whose <c>IKeeperRecoverable</c> sends still record into
    /// <see cref="CapturingSendProvider.SentKeeper"/>-shaped capture. Records keeper sends; throws on results.
    /// </summary>
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

    /// <summary>Options carrying the given execution-data TTL (other knobs at their defaults). The pipeline
    /// applies this <c>ExecutionDataTtl</c> (CONFIG-02/D-17) on the Post output write so terminal/orphaned
    /// data keys self-expire (the close-gate redis net-zero invariant depends on it).</summary>
    public static IOptions<ProcessorLivenessOptions> Options(int executionDataTtlSeconds) =>
        Microsoft.Extensions.Options.Options.Create(new ProcessorLivenessOptions
        {
            ExecutionDataTtlSeconds = executionDataTtlSeconds,
        });

    /// <summary>The retry budget the pipeline consumes (Limit immediate attempts per L2 op + per send).</summary>
    public static IOptions<RetryOptions> Retry(int limit = 3) =>
        Microsoft.Extensions.Options.Options.Create(new RetryOptions { Limit = limit });

    /// <summary>
    /// A real <see cref="ProcessorMetrics"/> for the hermetic facts — built from a live
    /// <see cref="IMeterFactory"/>. No collector is wired, so the increments are no-ops in-test; this just
    /// satisfies the non-null ctor dependency (mirrors <c>OrchestratorTestStubs.Metrics()</c>).
    /// </summary>
    public static ProcessorMetrics Metrics()
    {
        var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        return new ProcessorMetrics(meterFactory);
    }

    /// <summary>
    /// An <see cref="EntryStepDispatch"/> with the given correlation + entry id. Phase 43 (D-04):
    /// <paramref name="entryId"/> is a <see cref="Guid"/> (the L2 data key); <see cref="Guid.Empty"/> is
    /// the no-input source-step sentinel (<c>SourceStep.IsSource</c>).
    /// </summary>
    public static EntryStepDispatch Dispatch(Guid entryId, Guid correlationId, string payload = "{\"cfg\":1}") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), payload)
        {
            CorrelationId = correlationId,
            EntryId = entryId,
        };

    /// <summary>
    /// An <see cref="ISendEndpointProvider"/> whose every resolved endpoint records each boxed message it is
    /// asked to <c>Send</c>: an <see cref="IStepResult"/> lands in <see cref="Sent"/> (orchestrator results),
    /// an <see cref="IKeeperRecoverable"/> lands in <see cref="SentKeeper"/> (Keeper-state messages). Both
    /// lists are order-preserving so a fact can assert e.g. the relative order of
    /// <c>SentKeeper.OfType&lt;KeeperInject&gt;()</c> vs <c>OfType&lt;KeeperDelete&gt;()</c>.
    /// </summary>
    public sealed class CapturingSendProvider : ISendEndpointProvider
    {
        public List<IStepResult> Sent { get; } = new();
        public List<IKeeperRecoverable> SentKeeper { get; } = new();

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            // The pipeline sends as (object)msg so MassTransit routes the runtime type; capture the object
            // overload and branch on the boxed contract type.
            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    var o = ci[0];
                    if (o is IStepResult sr) Sent.Add(sr);
                    else if (o is IKeeperRecoverable kr) SentKeeper.Add(kr);
                    return Task.CompletedTask;
                });
            return Task.FromResult(endpoint);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }
}
