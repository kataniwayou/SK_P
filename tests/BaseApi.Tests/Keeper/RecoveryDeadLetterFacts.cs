using BaseConsole.Core.Messaging;
using global::Keeper;
using global::Keeper.Recovery;
using MassTransit;
using MassTransit.Middleware;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// KEEP-05 / D-01 (Dlq1 mode): a Keeper REINJECT whose L2 read raises a Redis INFRASTRUCTURE fault
/// (NOT an absent/empty key — that is now a by-design DROP per Phase 52 D-06) must FAULT through the
/// RetryLoop Guard on exhaustion, so the give-up routes to the consolidated skp-dlq-1 via the inherited
/// <see cref="ConsolidatedErrorTransportFilter"/>, rather than spinning forever. This wires the real
/// <see cref="ReinjectConsumer"/> on an in-memory <see cref="ITestHarness"/> with a throwing L2,
/// reproducing the BaseConsole.Core consolidated error pipeline (the same wiring proven in
/// <c>KeeperDlqConsolidationTests</c>), and asserts the message both faults AND lands in the consolidated
/// dead-letter sink.
/// <para>
/// Hermetic scope: the in-memory transport cannot exercise the RabbitMQ-specific skp-dlq-1 queue/TTL — the
/// literal-queue + serialization proof defers to the RealStack close gate. The automated KEEP-05/D-01 gate
/// here is: (1) the infra-fault case is observed as a faulted consume, and (2) the consolidated error
/// transport moves it to the single skp-dlq-1 endpoint as a typed <see cref="ConsolidatedFault"/> (NOT a
/// silent ack, NOT per-{queue}_error).
/// </para>
/// </summary>
public sealed class RecoveryDeadLetterFacts
{
    /// <summary>An L2 whose StringLengthAsync raises a Redis INFRASTRUCTURE exception — the op-exhaustion
    /// path that, under the default Dlq1 policy, dead-letters (distinct from the absent/empty STRLEN==0
    /// by-design drop, D-06).</summary>
    private static IConnectionMultiplexer ThrowingMux()
    {
        var db = Substitute.For<IDatabase>();
        db.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<long>>(_ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "L2 down"));
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    private static ServiceProvider BuildHarness(int retryLimit) =>
        new ServiceCollection()
            .AddLogging()
            .AddMetrics()
            .AddSingleton(ThrowingMux())
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>))
            .AddSingleton<global::Keeper.Observability.KeeperMetrics>()
            .AddSingleton(Options.Create(new RetryOptions { Limit = retryLimit }))
            .AddSingleton(Options.Create(new RecoveryOptions { PartitionCount = 8 }))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<ReinjectConsumer>();

                // The consolidated forensic sink so the moved ConsolidatedFault is observable.
                x.AddHandler((ConsumeContext<ConsolidatedFault> _) => Task.CompletedTask)
                    .Endpoint(e => e.Name = ConsolidatedErrorTransportFilter.Dlq1);

                // The SUT pipeline — identical to AddBaseConsoleMessaging's wiring (DLQ-04): bounded
                // immediate retry then the consolidated error move (so an exhausted/faulted delivery routes
                // to the single skp-dlq-1, exactly as a Keeper recovery give-up does in the real stack).
                x.AddConfigureEndpointsCallback((context, name, e) =>
                {
                    e.UseMessageRetry(r => r.Immediate(retryLimit));
                    e.ConfigureError(ep =>
                    {
                        ep.UseFilter(new GenerateFaultFilter());
                        ep.UseFilter(new ConsolidatedErrorTransportFilter());
                    });
                });

                x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
            })
            .BuildServiceProvider(true);

    [Fact]
    [Trait("Phase", "52")]
    public async Task InfraFault_reinject_faults_and_routes_to_dead_letter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildHarness(retryLimit: 1);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var msg = new KeeperReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
            {
                CorrelationId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                EntryId = Guid.NewGuid(),
                Payload = "step-config",
            };
            await harness.Bus.Publish(msg, ct);

            // The consumer's L2 read raises an infra fault → through Guard on exhaustion → faulted consume
            // (NOT a silent ack, NOT a drop — drops are the absent/empty STRLEN==0 case, D-06).
            Assert.True(await harness.Consumed.Any<KeeperReinject>(f => f.Exception is not null, ct));

            // On exhaustion the consolidated error transport moves it to the ONE shared skp-dlq-1 endpoint
            // (consolidated dead-letter), NOT per-{queue}_error.
            Assert.True(await harness.Consumed.Any<ConsolidatedFault>(ct));
        }
        finally { await harness.Stop(ct); }
    }

    // ----- RESIL-03 (R3): at-least-once / no-collapse on duplicate KeeperReinject delivery ----------

    /// <summary>An L2 whose StringLengthAsync reports the recovered key PRESENT (non-zero) so REINJECT
    /// confirms the data and re-injects (the success path — the effect we prove reproduces).</summary>
    private static IConnectionMultiplexer PresentMux()
    {
        var db = Substitute.For<IDatabase>();
        db.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(7L);   // present, non-empty
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// Phase 47 / RESIL-03: the REINJECT recovery path is at-least-once and carries NO dedup, so delivering
    /// the SAME <see cref="KeeperReinject"/> (identical ids) TWICE into ONE <see cref="ReinjectConsumer"/>
    /// reproduces its re-injection effect TWICE — the reconstructed <see cref="EntryStepDispatch"/> is sent
    /// twice (Sent.Count == 2), no collapse, no throw.
    /// </summary>
    [Fact]
    [Trait("Phase", "47")]
    public async Task Duplicate_Reinject_reproduces_effect_no_collapse()
    {
        var ct = TestContext.Current.CancellationToken;

        var send = new RecoveryTestKit.CapturingSendProvider();
        var consumer = new ReinjectConsumer(
            PresentMux(), send,
            Options.Create(new RetryOptions { Limit = 1 }),
            Options.Create(new RecoveryOptions { PartitionCount = 8 }),
            RecoveryTestKit.Metrics(), NullLogger<ReinjectConsumer>.Instance);

        var msg = new KeeperReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            EntryId = Guid.NewGuid(),
            Payload = "step-config",
        };

        // Deliver the SAME message TWICE into ONE consumer (at-least-once redelivery shape).
        await consumer.Consume(ContextFor(msg, ct));
        await consumer.Consume(ContextFor(msg, ct));

        // No collapse: the second identical delivery is NOT deduped — the reconstructed EntryStepDispatch is
        // re-injected twice, no throw. Both target the SAME origin queue:{ProcessorId:D}.
        Assert.Equal(2, send.Sent.Count);
        Assert.All(send.Sent, s => Assert.Equal($"queue:{msg.ProcessorId:D}", s.Uri.ToString()));
        Assert.All(send.Sent, s => Assert.IsType<EntryStepDispatch>(s.Message));
    }

    /// <summary>A ConsumeContext substitute carrying <paramref name="message"/> and a cancellation token.</summary>
    private static ConsumeContext<T> ContextFor<T>(T message, CancellationToken ct)
        where T : class
    {
        var context = Substitute.For<ConsumeContext<T>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(ct);
        return context;
    }
}
