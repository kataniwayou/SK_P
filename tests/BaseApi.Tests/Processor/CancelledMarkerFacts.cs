using System.Diagnostics.Metrics;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Processing;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Core;
using StackExchange.Redis;
using Xunit;
using ExecutionResult = Messaging.Contracts.ExecutionResult;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 32 Plan 04 Task 2 (req-2; D-02/D-07). Hermetic facts pinning the breaker MARKER write shape: the
/// no-TTL <c>expiry: null</c> at the call site (a TTL'd marker would be a self-expiring breaker, Pitfall
/// 3). The hermetic test asserts the captured expiry argument is null; the live <c>TTL == -1</c> assertion
/// is owned by Plan 06's E2E.
/// </summary>
public sealed class CancelledMarkerFacts
{
    private const int Limit = 3;
    private const string Output = "{\"v\":1}";

    private sealed class ThrowingSendProvider : ISendEndpointProvider
    {
        public Task<ISendEndpoint> GetSendEndpoint(Uri address)
        {
            var endpoint = Substitute.For<ISendEndpoint>();
            endpoint.Send(Arg.Any<ExecutionResult>(), Arg.Any<CancellationToken>())
                .Returns<Task>(_ => throw new InvalidOperationException("stub: broker Send unreachable (infra)"));
            return Task.FromResult(endpoint);
        }
        public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
    }

    private static ProcessorMetrics Metrics(out ServiceProvider provider)
    {
        provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return new ProcessorMetrics(provider.GetRequiredService<IMeterFactory>());
    }

    [Fact]
    public async Task MarkerWrite_Uses_NoTtl_ExpiryNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatch = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":1}")
        {
            CorrelationId = Guid.NewGuid(),
            EntryId = "",
            H = "feedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedface",
        };
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
            Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>()).Returns(true);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var processor = new DispatchTestKit.FakeProcessor(DispatchTestKit.Results(Output));
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var send = new ThrowingSendProvider();
        var metrics = Metrics(out var provider);
        using (provider)
        {
            var consumer = new EntryStepDispatchConsumer(
                mux, context, processor, DispatchTestKit.Options(300), send, metrics,
                DispatchTestKit.Retry(Limit), NullLogger<EntryStepDispatchConsumer>.Instance);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                consumer.Consume(DispatchTestKit.RetryContext(dispatch, Limit, ct)));
        }

        // Find the marker SET call and assert its expiry/Expiration argument is the no-TTL sentinel.
        // The `expiry: null` named arg binds the (RedisKey, RedisValue, Expiration, ValueCondition,
        // CommandFlags) overload; a null TimeSpan? converts to Expiration.None (KeepTtl=false, no TTL).
        var markerCall = db.ReceivedCalls().Single(c =>
            c.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync)
            && c.GetArguments().Length >= 2
            && c.GetArguments()[0] is RedisKey k && k.ToString() == L2ProjectionKeys.Cancelled(dispatch.WorkflowId)
            && c.GetArguments()[1] is RedisValue v && v == L2ProjectionKeys.CancelledMarkerValue);

        var args = markerCall.GetArguments();
        // The 3rd positional arg is the expiry. The marker binds the modern Expiration overload with
        // Expiration.Persist (no TTL), which renders "PERSIST". A finite TTL (TimeSpan/EX) would render
        // "EX 300" (the ExecutionDataTtlSeconds=300 form). Assert the no-TTL form, NOT a finite EX.
        var expiryArg = args[2];
        Assert.NotNull(expiryArg);                          // Expiration is a struct, boxed non-null
        Assert.Equal("PERSIST", expiryArg!.ToString());     // no TTL (D-07 / Pitfall 3)
        Assert.DoesNotContain("EX", expiryArg.ToString());  // never a finite expiry
    }
}
