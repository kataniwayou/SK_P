using System.Diagnostics.Metrics;
using global::Keeper;
using global::Keeper.Health;
using global::Keeper.Observability;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Shared kit for the Phase-46 Keeper recovery-consumer facts: an already-open <see cref="IL2HealthGate"/>
/// (so the body runs immediately past the base D-03 gate-wait), the <see cref="RetryOptions"/>/
/// <see cref="RecoveryOptions"/> option helpers, a fake <see cref="IDatabase"/>
/// behind a substituted <see cref="IConnectionMultiplexer"/>, and a <see cref="CapturingSendProvider"/>
/// recording boxed <see cref="IStepResult"/> / <see cref="IKeeperRecoverable"/> / <see cref="EntryStepDispatch"/>
/// sends with the endpoint URI each was sent to.
/// </summary>
internal static class RecoveryTestKit
{
    /// <summary>A gate that is already open — WaitForOpenAsync returns synchronously. Phase 52 (D-04/D-09)
    /// removed the base gate-wait, so the recovery consumers no longer take an IL2HealthGate; this helper
    /// survives only for the remaining non-recovery-consumer references (e.g. BitHealthLoop tests).</summary>
    public static IL2HealthGate OpenGate()
    {
        var gate = Substitute.For<IL2HealthGate>();
        gate.WaitForOpenAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return gate;
    }

    public static IOptions<RetryOptions> Retry(int limit = 3) =>
        Options.Create(new RetryOptions { Limit = limit });

    /// <summary>WR-02 (Phase 71): the Keeper RecoveryOptions helper carrying the ExecutionDataTtlSeconds knob
    /// the OrchestratorInjectConsumer reads to TTL the copied data key (mirrors the orchestrator pipeline kit).</summary>
    public static IOptions<RecoveryOptions> Recovery(int executionDataTtlSeconds = 300) =>
        Options.Create(new RecoveryOptions { ExecutionDataTtlSeconds = executionDataTtlSeconds });

    /// <summary>A real <see cref="KeeperMetrics"/> built from a real <see cref="IMeterFactory"/> (mirrors
    /// the ProcessorMetricsFacts construction idiom) so consumer facts can pass a live counter and observe
    /// it via a <see cref="System.Diagnostics.Metrics.MeterListener"/>.</summary>
    public static KeeperMetrics Metrics()
    {
        var meterFactory = new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        return new KeeperMetrics(meterFactory);
    }

    /// <summary>A multiplexer over a caller-supplied (or default) substituted <see cref="IDatabase"/>.</summary>
    public static IConnectionMultiplexer Mux(IDatabase db)
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>A fake db whose StringGetAsync resolves the registered keys (absent → RedisValue.Null) and
    /// whose StringSetAsync/KeyDeleteAsync succeed (stubbed so Received() assertions work).</summary>
    public static IDatabase Db(IReadOnlyDictionary<string, string>? values = null)
    {
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var key = ((RedisKey)ci[0]).ToString();
                return values is not null && values.TryGetValue(key, out var v) ? (RedisValue)v : RedisValue.Null;
            });
        db.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(true);
        // D-10 / WR-01: SE.Redis 2.13.1 binds the production 2-arg StringSetAsync(key, data) call to the
        // 5-arg Expiration/ValueCondition overload (the 6-arg overload above is dead) — stub it explicitly
        // so the INJECT write path is observable rather than relying on NSubstitute default-return.
        db.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
                Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())
            .Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>()).Returns(2L);   // A19: both-key DEL count removed
        return db;
    }

    /// <summary>An <see cref="ISendEndpointProvider"/> recording each boxed message + the endpoint URI it
    /// was sent to. The recovery bodies send the concrete record (NOT boxed as object), so capture both the
    /// generic and the object Send overloads.</summary>
    public sealed class CapturingSendProvider : ISendEndpointProvider
    {
        public List<(Uri Uri, object Message)> Sent { get; } = new();

        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<EntryStepDispatch>(), Arg.Any<CancellationToken>())
                .Returns(ci => { Sent.Add((address, ci[0]!)); return Task.CompletedTask; });
            endpoint.Send(Arg.Any<StepCompleted>(), Arg.Any<CancellationToken>())
                .Returns(ci => { Sent.Add((address, ci[0]!)); return Task.CompletedTask; });
            // RESEARCH A6: OrchestratorReinjectConsumer re-sends the reconstructed result boxed as object so a
            // single Send overload carries every IStepResult subtype. Capture the boxed-object Send generically
            // so StepFailed/StepCancelled/StepProcessing (and StepCompleted via the boxed path) are all recorded.
            endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>())
                .Returns(ci => { Sent.Add((address, ci[0]!)); return Task.CompletedTask; });
            return Task.FromResult(endpoint);
        }

        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }
}
