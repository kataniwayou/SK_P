using System.Diagnostics.Metrics;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.L1;
using Orchestrator.Observability;
using StackExchange.Redis;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;   // disambiguate from MassTransit.ExecutionResult

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 32.1 (req-2 dedup) — the retained Phase-31 orchestrator-hop dedup coverage:
/// <list type="bullet">
///   <item><b>Dedup counter (D-10):</b> the existing <c>flag[m.H]=="Ack"</c> drop gate increments
///   <c>orchestrator_result_deduped</c> exactly once (tagged ProcessorId) and still drops — no dispatch,
///   no flip.</item>
/// </list>
/// Analog: <see cref="ResultAckTests"/> + <see cref="ResultConsumeTests"/>.
/// </summary>
public sealed class ResultCheckAndDropFacts
{
    private static StepProjection Step(int entryCondition, Guid processorId, string payload, params Guid[] nextStepIds) =>
        new(EntryCondition: entryCondition, ProcessorId: processorId, Payload: payload, NextStepIds: [.. nextStepIds]);

    private static void SeedWorkflow(
        WorkflowL1Store store, Guid workflowId, IReadOnlyDictionary<Guid, StepProjection> steps)
    {
        var entry = new WorkflowL1([], "*/5 * * * *", Guid.NewGuid(), steps)
        {
            Liveness = new LivenessProjection(DateTime.UtcNow, Interval: 300, Status: "active"),
        };
        store.Upsert(workflowId, entry);
    }

    /// <summary>Builds a ResultConsumer over the supplied redis mux + a real (no-collector) metrics holder.</summary>
    private static ResultConsumer Build(
        WorkflowL1Store store, IStepDispatcher dispatcher, IConnectionMultiplexer redis, OrchestratorMetrics metrics)
        => new(store, new StepAdvancement(), dispatcher, redis, metrics, NullLogger<ResultConsumer>.Instance);

    /// <summary>
    /// A disposable scope holding a real <see cref="OrchestratorMetrics"/> whose
    /// <c>orchestrator_result_deduped</c> increments are observed via a scoped <see cref="MeterListener"/>.
    /// Both the listener and the meter-factory provider are bound to THIS instance's lifetime (the
    /// .NET 8 blessed pattern — mirrors <see cref="BreakerMetricsFacts"/>) so nothing leaks across the
    /// parallel-by-default xUnit test runs (a leaked listener would cross-count the shared instrument).
    /// Filters measurements to THIS holder's Meter instance, not just the instrument name, so a sibling
    /// test's "Orchestrator" meter cannot bleed into this count.
    /// </summary>
    private sealed class DedupScope : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly MeterListener _listener;
        private long _count;
        private readonly List<string> _tagKeys = [];

        public OrchestratorMetrics Metrics { get; }
        public long DedupCount => Interlocked.Read(ref _count);
        public IReadOnlyList<string> DedupTagKeys => _tagKeys;

        public DedupScope()
        {
            _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            Metrics = new OrchestratorMetrics(_provider.GetRequiredService<IMeterFactory>());

            var instrument = Metrics.ResultDeduped;
            _listener = new MeterListener
            {
                InstrumentPublished = (i, l) =>
                {
                    if (ReferenceEquals(i, instrument))
                        l.EnableMeasurementEvents(i);
                },
            };
            _listener.SetMeasurementEventCallback<long>((i, measurement, tags, state) =>
            {
                Interlocked.Add(ref _count, measurement);
                lock (_tagKeys)
                    foreach (var t in tags)
                        _tagKeys.Add(t.Key);
            });
            _listener.Start();
        }

        public void Dispose()
        {
            _listener.Dispose();
            _provider.Dispose();
        }
    }

    // ----- 1. flag[m.H]=="Ack" -> ONE dedup increment, dropped (no dispatch) ----------------------

    [Fact]
    public async Task FlagAlreadyAck_IncrementsDedupOnce_Drops_NoDispatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<IStepDispatcher>();

        var workflowId = Guid.NewGuid();
        var completedStepId = Guid.NewGuid();
        var nextStepId = Guid.NewGuid();

        var steps = new Dictionary<Guid, StepProjection>
        {
            [completedStepId] = Step(0, Guid.NewGuid(), "{}", nextStepId),
            [nextStepId] = Step((int)StepOutcome.Completed, Guid.NewGuid(), "{}"),
        };
        var store = new WorkflowL1Store();
        SeedWorkflow(store, workflowId, steps);

        var resultH = "ac4eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => ((RedisKey)ci[0]).ToString() == L2ProjectionKeys.Flag(resultH) ? (RedisValue)"Ack" : RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        using var scope = new DedupScope();
        var consumer = Build(store, dispatcher, mux, scope.Metrics);
        var result = new ExecutionResult(workflowId, completedStepId, Guid.NewGuid(), StepOutcome.Completed)
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = Guid.NewGuid().ToString("N"),
            H = resultH,
        };

        await consumer.Consume(OrchestratorTestStubs.Context(result, ct));

        // Exactly ONE orchestrator_result_deduped increment at the flag-Ack gate, tagged ProcessorId.
        Assert.Equal(1, scope.DedupCount);
        Assert.Contains("ProcessorId", scope.DedupTagKeys);

        // Dropped — no dispatch.
        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // No flip / no write of any kind on the dropped path.
        await db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
            Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }
}
