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
/// SYMMETRIC-KEEPER-EXEC-PATH: a Keeper REINJECT whose L2 read raises a Redis INFRASTRUCTURE fault
/// (NOT an absent/empty key — that is now a by-design DROP per Phase 52 D-06) must FAULT through the
/// RetryLoop Guard on exhaustion, and the faulted consume must NOT be acked — it falls through to broker
/// nack-requeue. The keeper-recovery endpoint is symmetric with the processor dispatch / orchestrator
/// result endpoints: it carries NO bus <c>UseMessageRetry</c> and NO <c>ConfigureError</c>, so a
/// Guard-exhaust throw falls out of <c>Consume</c> to broker nack-requeue. There is NO dead-letter tier at
/// all — the consolidated <c>skp-dlq-1</c> queue was deleted (dead code), so nothing to dead-letter into.
/// This wires the real <see cref="ProcessorReinjectConsumer"/> on an in-memory <see cref="ITestHarness"/> with a
/// throwing L2, on the production-shaped BARE endpoint (no retry/error callback), and asserts the message
/// faults (the nack-requeue shape).
/// <para>
/// Hermetic scope: in-memory proves the fault-and-no-ack SHAPE; broker-literal nack-requeue defers to the
/// RealStack close gate.
/// </para>
/// </summary>
public sealed class RecoveryDeadLetterFacts
{
    /// <summary>An L2 whose StringLengthAsync raises a Redis INFRASTRUCTURE exception — the op-exhaustion
    /// path that faults out of Consume (distinct from the absent/empty STRLEN==0 by-design drop, D-06).</summary>
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
                x.AddConsumer<ProcessorReinjectConsumer>();

                // NO AddConfigureEndpointsCallback wiring UseMessageRetry/ConfigureError — the production
                // keeper-recovery binder is now BARE (symmetric with the exec path). A Guard-exhaust throw
                // falls through to broker nack-requeue; nothing routes to skp-dlq-1.
                x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
            })
            .BuildServiceProvider(true);

    [Fact]
    [Trait("Phase", "52")]
    public async Task InfraFault_reinject_faults_and_does_not_dead_letter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildHarness(retryLimit: 1);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var msg = new ProcessorReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
            {
                CorrelationId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                EntryId = Guid.NewGuid(),
                Payload = "step-config",
            };
            await harness.Bus.Publish(msg, ct);

            // The consumer's L2 read raises an infra fault → through Guard on exhaustion → faulted consume
            // (NOT a silent ack, NOT a drop — drops are the absent/empty STRLEN==0 case, D-06). The faulted
            // delivery falls through to broker nack-requeue (no in-process retry, no error transport, and
            // no dead-letter tier — skp-dlq-1 was deleted as dead code). The faulted consume IS the shape.
            Assert.True(await harness.Consumed.Any<ProcessorReinject>(f => f.Exception is not null, ct));
        }
        finally { await harness.Stop(ct); }
    }

    // ----- RESIL-03 (R3): at-least-once / no-collapse on duplicate ProcessorReinject delivery ----------

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
    /// the SAME <see cref="ProcessorReinject"/> (identical ids) TWICE into ONE <see cref="ProcessorReinjectConsumer"/>
    /// reproduces its re-injection effect TWICE — the reconstructed <see cref="EntryStepDispatch"/> is sent
    /// twice (Sent.Count == 2), no collapse, no throw.
    /// </summary>
    [Fact]
    [Trait("Phase", "47")]
    public async Task Duplicate_Reinject_reproduces_effect_no_collapse()
    {
        var ct = TestContext.Current.CancellationToken;

        var send = new RecoveryTestKit.CapturingSendProvider();
        var consumer = new ProcessorReinjectConsumer(
            PresentMux(), send,
            Options.Create(new RetryOptions { Limit = 1 }),
            RecoveryTestKit.Metrics(), NullLogger<ProcessorReinjectConsumer>.Instance);

        var msg = new ProcessorReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
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
