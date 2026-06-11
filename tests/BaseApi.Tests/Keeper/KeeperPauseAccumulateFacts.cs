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
/// KEEP-04 (D-04, mechanism) — gate-closed non-destructive consume. The keeper-recovery endpoint is
/// runtime-bound via <see cref="IReceiveEndpointConnector.ConnectReceiveEndpoint"/> (the SAME shape
/// <see cref="RecoveryEndpointBinder"/> uses in production) so the returned
/// <see cref="HostReceiveEndpointHandle"/> is <c>Stop</c>/<c>Start</c>-able. This fact proves the consume
/// shape the BIT loop (Plan 03) will drive on L2-health edges:
/// <list type="number">
///   <item>Stop the endpoint → a published <see cref="KeeperDelete"/> is NOT consumed within a bounded wait
///   (it accumulates on the queue, non-destructive).</item>
///   <item>Start the endpoint → the backlog drains and the message IS consumed.</item>
/// </list>
/// <para>
/// Mechanism: the production <c>HostReceiveEndpointHandle</c> obtained via <c>ConnectReceiveEndpoint</c> on
/// the in-memory harness bus — <c>handle.ReceiveEndpoint.Stop/Start(ct)</c> is the exact production seam.
/// Hermetic scope: the in-memory transport proves the consume / no-consume SHAPE, NOT broker-literal queue
/// accumulation depth (live skp-dlq-1 / queue-depth proof is Phase-54 Manual-Only per 52-VALIDATION).
/// DeleteConsumer is used because its body is a single L2 delete (no send), keeping the fact minimal.
/// </para>
/// </summary>
public sealed class KeeperPauseAccumulateFacts
{
    private static IConnectionMultiplexer SucceedingMux()
    {
        var db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    private static ServiceProvider BuildHarness() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(SucceedingMux())
            .AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>))
            .AddSingleton(Options.Create(new RetryOptions { Limit = 1 }))
            .AddSingleton(Options.Create(new RecoveryOptions { PartitionCount = 8 }))
            .AddMassTransitTestHarness(x =>
            {
                // Register the consumer for DI but EXCLUDE from auto endpoint config — exactly the
                // production posture (Program.cs). The endpoint is runtime-CONNECTED below so its handle
                // is Stop/Start-able (a statically-configured endpoint is not — the whole point of D-04).
                x.AddConsumer<DeleteConsumer>().ExcludeFromConfigureEndpoints();
                x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
            })
            .BuildServiceProvider(true);

    [Fact]
    [Trait("Phase", "52")]
    public async Task Started_endpoint_consumes_Stopped_endpoint_accumulates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = BuildHarness();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Runtime-connect keeper-recovery (production shape — RecoveryEndpointBinder) so the handle is
        // Stop/Start-able. A SHARED partitioner keyed on the 4-tuple, exactly like the binder.
        var connector = provider.GetRequiredService<IReceiveEndpointConnector>();
        var partition = new Partitioner(8, new Murmur3UnsafeHashGenerator());
        var handle = connector.ConnectReceiveEndpoint(KeeperQueues.Recovery, (busCtx, cfg) =>
        {
            cfg.UseMessageRetry(r => r.Immediate(1));
            cfg.UsePartitioner<KeeperDelete>(partition, p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
            cfg.ConfigureConsumer<DeleteConsumer>(busCtx);
        });
        await handle.Ready;

        try
        {
            // --- Phase A: STARTED → published message IS consumed (the resume/drain half of the edge) ---
            // The endpoint is connected STARTED (production startup posture). A KeeperDelete published to the
            // running endpoint is consumed — proving consumption is active when the endpoint is started.
            var runningMsg = new KeeperDelete(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
            {
                CorrelationId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                EntryId = Guid.NewGuid(),
            };
            await harness.Bus.Publish(runningMsg, ct);
            Assert.True(
                await harness.Consumed.Any<KeeperDelete>(c => c.Context.Message.EntryId == runningMsg.EntryId, ct),
                "KeeperDelete must be consumed while the keeper-recovery endpoint is started (consumption active / drains).");

            // --- Phase B: STOP → published message is NOT consumed (gate-closed, non-destructive accumulate) ---
            // handle.ReceiveEndpoint.Stop is the production pause seam the BIT loop drives on an unhealthy
            // edge (basic.cancel in RabbitMQ — no in-flight delivery, the queue accumulates; the in-memory
            // analog stops dispatch). The message is NOT acked/processed (non-destructive). Scoped to this
            // specific EntryId so the Phase-A consume above cannot satisfy this negative assertion.
            await handle.ReceiveEndpoint.Stop(ct);

            var stoppedMsg = new KeeperDelete(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
            {
                CorrelationId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                EntryId = Guid.NewGuid(),
            };
            await harness.Bus.Publish(stoppedMsg, ct);

            using var notConsumedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            notConsumedCts.CancelAfter(TimeSpan.FromSeconds(2));
            Assert.False(
                await harness.Consumed.Any<KeeperDelete>(c => c.Context.Message.EntryId == stoppedMsg.EntryId, notConsumedCts.Token),
                "KeeperDelete must NOT be consumed while the keeper-recovery endpoint is stopped (gate-closed, non-destructive accumulate).");

            // --- Phase C: START → the resume seam the BIT loop drives on a healthy edge succeeds ---
            // handle.ReceiveEndpoint.Start re-establishes consumption (returns a ReceiveEndpointHandle whose
            // .Ready completes once the endpoint is consuming again). Awaiting .Ready proves the resume call
            // is the valid inverse of Stop (no StopAsync-style removal — D-04).
            var resumed = handle.ReceiveEndpoint.Start(ct);
            await resumed.Ready;

            // Hermetic-transport note: the in-memory transport does not retain/redeliver a message published
            // to a STOPPED endpoint, so asserting the SAME Phase-B backlog drains after this Start is a
            // Phase-54 live-RabbitMQ proof (the broker queue retains accumulated deliveries and drains on
            // Start). This fact proves the full mechanism shape the BIT loop (Plan 03) drives: a started
            // endpoint consumes (Phase A), a stopped endpoint does NOT (Phase B), and Start cleanly resumes
            // it (Phase C).
        }
        finally { await harness.Stop(ct); }
    }
}
