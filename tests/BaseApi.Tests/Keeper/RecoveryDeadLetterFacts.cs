using BaseConsole.Core.Messaging;
using global::Keeper;
using global::Keeper.Health;
using global::Keeper.Recovery;
using MassTransit;
using MassTransit.Middleware;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// KEEP-05 / D-04: a Keeper REINJECT whose recovered input data is GONE (the L2 read finds the key
/// absent/empty) must FAULT — surfacing the deliberate <see cref="RecoveryDataGoneException"/> terminal —
/// so the give-up routes to the consolidated skp-dlq-1 via the inherited
/// <see cref="ConsolidatedErrorTransportFilter"/>, rather than being silently acked. This wires the real
/// <see cref="ReinjectConsumer"/> on an in-memory <see cref="ITestHarness"/> with an already-open gate and
/// an EMPTY L2, reproducing the BaseConsole.Core consolidated error pipeline (the same wiring proven in
/// <c>KeeperDlqConsolidationTests</c>), and asserts the message both faults with the data-gone exception
/// AND lands in the consolidated dead-letter sink.
/// <para>
/// Hermetic scope: the in-memory transport cannot exercise the RabbitMQ-specific skp-dlq-1 queue/TTL — the
/// literal-queue + serialization proof defers to Phase-49 TEST-01 (VALIDATION.md Manual-Only row). The
/// automated KEEP-09/D-04 gate here is: (1) the data-gone case is observed as a faulted consume carrying
/// <see cref="RecoveryDataGoneException"/>, and (2) the consolidated error transport moves it to the single
/// skp-dlq-1 endpoint as a typed <see cref="ConsolidatedFault"/> (NOT a silent ack, NOT per-{queue}_error).
/// </para>
/// </summary>
public sealed class RecoveryDeadLetterFacts
{
    /// <summary>
    /// An empty L2 — the data-gone condition. ReinjectConsumer gates the data-gone terminal on
    /// StringLengthAsync (STRLEN == 0), so that is the method stubbed here; StringGetAsync is also
    /// nulled to keep the substitute internally consistent for any incidental reads.
    /// </summary>
    private static IConnectionMultiplexer EmptyMux()
    {
        var db = Substitute.For<IDatabase>();
        db.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(0L);
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    private static IL2HealthGate OpenGate()
    {
        var gate = Substitute.For<IL2HealthGate>();
        gate.WaitForOpenAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return gate;
    }

    private static ServiceProvider BuildHarness(int retryLimit) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(EmptyMux())
            .AddSingleton(OpenGate())
            .AddSingleton(Options.Create(new RetryOptions { Limit = retryLimit }))
            .AddSingleton(Options.Create(new RecoveryOptions { PartitionCount = 8, GateWaitSeconds = 300 }))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<ReinjectConsumer>();

                // The consolidated forensic sink so the moved ConsolidatedFault is observable.
                x.AddHandler((ConsumeContext<ConsolidatedFault> _) => Task.CompletedTask)
                    .Endpoint(e => e.Name = ConsolidatedErrorTransportFilter.Dlq1);

                // The SUT pipeline — identical to AddBaseConsoleMessaging's wiring (DLQ-04, D-06): bounded
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
    [Trait("Phase", "46")]
    [Trait("Phase", "47")]   // R2 re-tag: discoverable under --filter-trait "Phase=47" (cited, NOT re-tested)
    public async Task DataGone_reinject_faults_and_routes_to_dead_letter()
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

            // The consumer reads the (absent) L2 key and throws the deliberate data-gone terminal — observed
            // as a faulted consume carrying RecoveryDataGoneException (NOT a silent ack).
            Assert.True(await harness.Consumed.Any<KeeperReinject>(
                f => f.Exception is RecoveryDataGoneException, ct));

            // On exhaustion the consolidated error transport moves it to the ONE shared skp-dlq-1 endpoint
            // (consolidated dead-letter), NOT per-{queue}_error.
            Assert.True(await harness.Consumed.Any<ConsolidatedFault>(ct));
        }
        finally { await harness.Stop(ct); }
    }

    // ----- RESIL-03 (R3): at-least-once / no-collapse on duplicate KeeperReinject delivery ----------

    /// <summary>An L2 whose StringLengthAsync reports the recovered key PRESENT (non-zero) so REINJECT
    /// confirms the data and re-injects (the success path — the effect we prove reproduces, not the
    /// data-gone fault). Mirrors the absent EmptyMux() but for the present case.</summary>
    private static IConnectionMultiplexer PresentMux()
    {
        var db = Substitute.For<IDatabase>();
        db.StringLengthAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(7L);   // present, non-empty
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    /// <summary>
    /// Phase 47 / RESIL-03: the EntryStepDispatch-family recovery path (REINJECT, the seam available per
    /// Open Question 1 — DispatchTestKit does not exist) is at-least-once and carries NO dedup, so delivering
    /// the SAME <see cref="KeeperReinject"/> (identical ids) TWICE into ONE <see cref="ReinjectConsumer"/>
    /// reproduces its re-injection effect TWICE — the reconstructed <see cref="EntryStepDispatch"/> is sent
    /// twice (Sent.Count == 2), no collapse, no throw. Uses the documented consumer-level double-Consume +
    /// <see cref="RecoveryTestKit.CapturingSendProvider"/> fallback (the harness double-publish shape over
    /// EmptyMux would prove only the data-gone fault, not the success-effect reproduction).
    /// </summary>
    [Fact]
    [Trait("Phase", "47")]
    public async Task Duplicate_Reinject_reproduces_effect_no_collapse()
    {
        var ct = TestContext.Current.CancellationToken;

        var send = new RecoveryTestKit.CapturingSendProvider();
        var consumer = new ReinjectConsumer(
            PresentMux(), send, OpenGate(),
            Options.Create(new RetryOptions { Limit = 1 }),
            Options.Create(new RecoveryOptions { PartitionCount = 8, GateWaitSeconds = 300 }));

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
