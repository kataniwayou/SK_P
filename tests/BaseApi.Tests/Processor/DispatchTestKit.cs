using System.Diagnostics.Metrics;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;
using ExecutionResult = Messaging.Contracts.ExecutionResult;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Shared kit for the Plan 27-02 <see cref="EntryStepDispatchConsumer"/> outcome-matrix facts:
/// a configurable fake <see cref="BaseProcessorBase"/> (the transform seam), a consumer builder over
/// <c>OrchestratorTestStubs</c> Redis fakes + a <see cref="FakeProcessorContext"/> + an
/// <see cref="ProcessorLivenessOptions"/>, and Redis-multiplexer stubs whose <c>StringSetAsync</c>
/// (output write) can be made to throw (the write-fault infra case the orchestrator stubs don't cover).
/// </summary>
internal static class DispatchTestKit
{
    /// <summary>
    /// A test-double <see cref="BaseProcessorBase"/> whose <c>ProcessAsync</c> either returns a
    /// configurable list (recording the (inputData, config) it was called with) or throws.
    /// Mirrors <c>BaseProcessorSeamFacts.TestProcessor</c>.
    /// </summary>
    public sealed class FakeProcessor : BaseProcessorBase
    {
        private readonly Func<string, string, CancellationToken, Task<IReadOnlyList<ProcessResult>>> _impl;

        public FakeProcessor(IReadOnlyList<ProcessResult> results)
            => _impl = (input, config, _) =>
            {
                Invoked = true;
                LastInputData = input;
                LastConfig = config;
                return Task.FromResult(results);
            };

        public FakeProcessor(Exception toThrow)
            => _impl = (input, config, _) =>
            {
                Invoked = true;
                LastInputData = input;
                LastConfig = config;
                throw toThrow;
            };

        /// <summary>True once the transform was actually invoked (proves the before-ProcessAsync guards).</summary>
        public bool Invoked { get; private set; }
        public string? LastInputData { get; private set; }
        public string? LastConfig { get; private set; }

        protected override Task<IReadOnlyList<ProcessResult>> ProcessAsync(
            string inputData, string config, CancellationToken ct)
            => _impl(inputData, config, ct);
    }

    /// <summary>Returns N <see cref="ProcessResult"/>s carrying the given output strings.</summary>
    public static IReadOnlyList<ProcessResult> Results(params string[] outputs)
        => outputs.Select(o => new ProcessResult(o)).ToArray();

    /// <summary>
    /// A Redis multiplexer whose <c>StringGetAsync</c> resolves registered keys (PresentL2 shape) and
    /// whose <c>StringSetAsync</c> throws <see cref="RedisConnectionException"/> — the infra
    /// output-write-fault case (D-15). The orchestrator stubs only fault the read.
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
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>Options carrying the given execution-data TTL (other knobs at their defaults).</summary>
    public static IOptions<ProcessorLivenessOptions> Options(int executionDataTtlSeconds) =>
        Microsoft.Extensions.Options.Options.Create(new ProcessorLivenessOptions
        {
            ExecutionDataTtlSeconds = executionDataTtlSeconds,
        });

    /// <summary>
    /// A real <see cref="ProcessorMetrics"/> for the hermetic facts — built from a live
    /// <see cref="IMeterFactory"/> (Plan 30-03 added the metrics ctor param to EntryStepDispatchConsumer).
    /// No collector is wired, so the increments are no-ops in-test; this just satisfies the non-null
    /// ctor dependency (mirrors <c>OrchestratorTestStubs.Metrics()</c>).
    /// </summary>
    public static ProcessorMetrics Metrics()
    {
        var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        return new ProcessorMetrics(meterFactory);
    }

    /// <summary>Builds the consumer over the supplied collaborators (NullLogger + given send provider).</summary>
    public static EntryStepDispatchConsumer Build(
        IConnectionMultiplexer redis,
        IProcessorContext context,
        BaseProcessorBase processor,
        ISendEndpointProvider sendProvider,
        int executionDataTtlSeconds = 300) =>
        new(redis, context, processor, Options(executionDataTtlSeconds), sendProvider,
            Metrics(), NullLogger<EntryStepDispatchConsumer>.Instance);

    /// <summary>An <see cref="EntryStepDispatch"/> with the given correlation + entry id.</summary>
    public static EntryStepDispatch Dispatch(Guid entryId, Guid correlationId, string payload = "{\"cfg\":1}") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), payload)
        {
            CorrelationId = correlationId,
            EntryId = entryId,
        };

    /// <summary>
    /// An <see cref="ISendEndpointProvider"/> whose every resolved endpoint records each
    /// <see cref="ExecutionResult"/> it is asked to <c>Send</c> into <see cref="Sent"/> (order-preserving).
    /// Used by the NSubstitute/unit facts (the harness is used for the real Send pipeline cases).
    /// </summary>
    public sealed class CapturingSendProvider : ISendEndpointProvider
    {
        public List<ExecutionResult> Sent { get; } = new();

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<ExecutionResult>(), Arg.Any<CancellationToken>())
                .Returns(ci => { Sent.Add((ExecutionResult)ci[0]); return Task.CompletedTask; });
            return Task.FromResult(endpoint);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }

    /// <summary>
    /// A synthetic consumer bound to the <c>orchestrator-result</c> endpoint so a real harness <c>Send</c>
    /// to <c>queue:orchestrator-result</c> is captured (mirrors <c>ResultConsumeTests.CapturingDispatchConsumer</c>).
    /// </summary>
    public sealed class CapturingResultConsumer : IConsumer<ExecutionResult>
    {
        public Task Consume(ConsumeContext<ExecutionResult> context) => Task.CompletedTask;
    }

    /// <summary>
    /// Builds an in-memory MassTransit harness binding <see cref="CapturingResultConsumer"/> on the
    /// short-name <c>orchestrator-result</c> endpoint (the queue a <c>Send</c> to
    /// <c>queue:orchestrator-result</c> lands on). The consumer under test uses <c>harness.Bus</c> as its
    /// <see cref="ISendEndpointProvider"/>; captured <see cref="ExecutionResult"/>s are read via
    /// <c>harness.Consumed</c>.
    /// </summary>
    public static ServiceProvider BuildResultHarness() =>
        new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<CapturingResultConsumer>();
                x.UsingInMemory((ctx, cfg) =>
                {
                    cfg.ReceiveEndpoint(OrchestratorQueues.Result,
                        e => e.ConfigureConsumer<CapturingResultConsumer>(ctx));
                    cfg.ConfigureEndpoints(ctx);
                });
            })
            .BuildServiceProvider(true);
}
