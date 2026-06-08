using System.Diagnostics.Metrics;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
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
    /// <summary>
    /// A test-double <see cref="BaseProcessorBase"/> whose In-Process <c>ProcessAsync</c> either returns a
    /// configurable <see cref="List{ProcessItem}"/> (recording the (validatedData, payload) it was called
    /// with) or throws (the throw ctor serves BOTH the <c>ProcessStatusException</c> family AND the
    /// unexpected-exception case).
    /// </summary>
    public sealed class FakeProcessor : BaseProcessorBase
    {
        private readonly Func<string, string, CancellationToken, Task<List<ProcessItem>>> _impl;

        public FakeProcessor(List<ProcessItem> items)
            => _impl = (validatedData, payload, _) =>
            {
                Invoked = true;
                LastInputData = validatedData;
                LastConfig = payload;
                return Task.FromResult(items);
            };

        public FakeProcessor(Exception toThrow)
            => _impl = (validatedData, payload, _) =>
            {
                Invoked = true;
                LastInputData = validatedData;
                LastConfig = payload;
                throw toThrow;
            };

        /// <summary>True once the transform was actually invoked (proves the Pre guards short-circuited or not).</summary>
        public bool Invoked { get; private set; }
        public string? LastInputData { get; private set; }
        public string? LastConfig { get; private set; }

        protected override Task<List<ProcessItem>> ProcessAsync(
            string validatedData, string payload, CancellationToken ct)
            => _impl(validatedData, payload, ct);
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
        // KeyDeleteAsync is a no-op success (the end-delete finally runs and succeeds on this path).
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
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
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// A Redis multiplexer whose <c>StringGetAsync</c> resolves registered keys, <c>StringSetAsync</c>
    /// (output write) succeeds, but <c>KeyDeleteAsync</c> (the end-delete) throws
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
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: Redis delete unreachable");
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw boom);
        db.When(x => x.KeyDeleteAsync(Arg.Any<RedisKey>()))
            .Do(_ => throw boom);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>Options carrying the given execution-data TTL (other knobs at their defaults). Retained for
    /// any caller still constructing <see cref="ProcessorLivenessOptions"/>; the Phase-44 pipeline drops
    /// the TTL on the Post write.</summary>
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
    /// lists are order-preserving so a fact can assert e.g. <c>SentKeeper.OfType&lt;KeeperUpdate&gt;()</c>
    /// precedes <c>OfType&lt;KeeperCleanup&gt;()</c>.
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
