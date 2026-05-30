using BaseConsole.Core.Messaging;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// CORR-01 / CORR-02 / D-02 fact #6: both correlation filters exercised standalone via
/// <c>AddMassTransitTestHarness</c> (in-memory transport — no RabbitMQ).
/// <list type="bullet">
///   <item>INBOUND (CORR-01): the inbound consume filter populates the ambient
///         <see cref="ICorrelationAccessor"/> from the envelope <c>CorrelationId</c> before the consumer
///         body runs — the probe consumer reads a non-empty accessor value.</item>
///   <item>OUTBOUND (CORR-02 / D-01): the outbound send/publish filter stamps the envelope
///         <c>CorrelationId</c> with the ambient Guid-parseable id for an <see cref="ICorrelated"/> message,
///         without mutating the record body. The published envelope carries that exact Guid.</item>
/// </list>
/// </summary>
public sealed class ConsoleCorrelationFilterTests
{
    /// <summary>A minimal <see cref="ICorrelated"/> message for the harness (6 get-only Guids — D-01).</summary>
    public sealed record ProbeMessage(
        Guid CorrelationId,
        Guid ExecutionId,
        Guid WorkflowId,
        Guid StepId,
        Guid ProcessorId,
        Guid EntryId) : ICorrelated;

    /// <summary>
    /// Probe consumer: captures the ambient correlation accessor value AT CONSUME TIME so the test can prove
    /// the inbound filter populated it before the body ran (CORR-01).
    /// </summary>
    public sealed class ProbeConsumer(ICorrelationAccessor accessor) : IConsumer<ProbeMessage>
    {
        public static volatile string? CapturedAccessorValue;

        public Task Consume(ConsumeContext<ProbeMessage> context)
        {
            CapturedAccessorValue = accessor.Get();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Inbound_Filter_Populates_Accessor_And_Outbound_Stamps_Envelope()
    {
        var ct = TestContext.Current.CancellationToken;
        ProbeConsumer.CapturedAccessorValue = null;

        // Guid-parseable ambient id (Open-Q 2 / A3) so the outbound filter sets context.CorrelationId.
        var ambientId = Guid.NewGuid();

        await using var provider = new ServiceCollection()
            .AddSingleton<ICorrelationAccessor, AsyncLocalCorrelationAccessor>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<ProbeConsumer>();
                x.UsingInMemory((ctx, cfg) =>
                {
                    // All three correlation filters bus-wide — the same wiring AddBaseConsoleMessaging uses.
                    cfg.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx);     // CORR-01
                    cfg.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx);          // CORR-02
                    cfg.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);    // CORR-02
                    cfg.ConfigureEndpoints(ctx);
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        var accessor = provider.GetRequiredService<ICorrelationAccessor>();
        await harness.Start();
        try
        {
            // Set the ambient id on the publishing async-context so the OUTBOUND filter stamps the envelope.
            accessor.Set(ambientId.ToString());

            var message = new ProbeMessage(
                CorrelationId: Guid.Empty,   // body left empty — the filter stamps the ENVELOPE, not the body (D-01)
                ExecutionId: Guid.NewGuid(),
                WorkflowId: Guid.NewGuid(),
                StepId: Guid.NewGuid(),
                ProcessorId: Guid.NewGuid(),
                EntryId: Guid.NewGuid());

            await harness.Bus.Publish(message, ct);

            // OUTBOUND (CORR-02): the published envelope CorrelationId equals the ambient Guid.
            Assert.True(
                await harness.Published.Any<ProbeMessage>(p => p.Context.CorrelationId == ambientId, ct),
                "Outbound filter did not stamp the published envelope CorrelationId with the ambient Guid.");

            // The message must have been consumed (so the inbound filter ran).
            Assert.True(await harness.Consumed.Any<ProbeMessage>(ct));

            // INBOUND (CORR-01): the consumer observed a populated accessor value (set by the inbound filter
            // from the envelope CorrelationId, which the outbound filter stamped to the ambient Guid).
            Assert.Equal(ambientId.ToString(), ProbeConsumer.CapturedAccessorValue);
        }
        finally
        {
            await harness.Stop(ct);
        }
    }
}
