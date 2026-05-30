using BaseConsole.Core.Messaging;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// CORR-01 (amended, D-01): the inbound consume filter reads the correlation id from the message
/// BODY (<see cref="ICorrelated.CorrelationId"/>), not the MassTransit envelope. It populates the
/// ambient <see cref="ICorrelationAccessor"/> and opens the <c>"CorrelationId"</c> MEL log scope
/// from that body value before the consumer body runs.
/// <list type="bullet">
///   <item>BODY-SOURCED (CORR-01): publishing a <c>ProbeMessage(someGuid)</c> → the consumer reads
///         <c>someGuid.ToString()</c> off the accessor (the body value, NOT the envelope).</item>
///   <item>TOLERANCE: a message that does NOT implement <see cref="ICorrelated"/> is still consumed
///         without throwing (<c>where T : class</c> + <c>as ICorrelated</c> null-safe fallback).</item>
/// </list>
/// </summary>
public sealed class ConsoleCorrelationFilterTests
{
    /// <summary>A minimal <see cref="ICorrelated"/> message for the harness (slim 1-Guid contract — D-01).</summary>
    public sealed record ProbeMessage(Guid CorrelationId) : ICorrelated;

    /// <summary>A non-correlated message — does NOT implement <see cref="ICorrelated"/> (filter-tolerance case).</summary>
    public sealed record PlainMessage(string Text);

    /// <summary>
    /// Probe consumer: captures the ambient correlation accessor value AT CONSUME TIME so the test can prove
    /// the inbound filter populated it from the body before the body ran (CORR-01).
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

    /// <summary>Consumer for the non-correlated message — must run to completion (filter tolerates it).</summary>
    public sealed class PlainConsumer : IConsumer<PlainMessage>
    {
        public Task Consume(ConsumeContext<PlainMessage> context) => Task.CompletedTask;
    }

    [Fact]
    public async Task Inbound_Filter_Populates_Accessor_From_Body()
    {
        var ct = TestContext.Current.CancellationToken;
        ProbeConsumer.CapturedAccessorValue = null;

        // Fixed body correlation id — the inbound filter must read THIS off the body, not the envelope.
        var bodyId = Guid.NewGuid();

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

        // A DIFFERENT envelope correlation id, set explicitly at publish time. The body read must win:
        // if the filter read the envelope (the pre-D-01 behavior), the accessor would equal envelopeId, not bodyId.
        // This makes the test a genuine discriminator between body-read and envelope-read (MassTransit's
        // by-convention envelope population from the CorrelationId property would otherwise mask the difference).
        var envelopeId = Guid.NewGuid();
        Assert.NotEqual(bodyId, envelopeId);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // Publish a message whose BODY carries bodyId, but stamp a DIFFERENT envelope CorrelationId.
            await harness.Bus.Publish(new ProbeMessage(bodyId), ctx => ctx.CorrelationId = envelopeId, ct);

            // The message must have been consumed (so the inbound filter ran).
            Assert.True(await harness.Consumed.Any<ProbeMessage>(ct));

            // INBOUND (CORR-01, D-01): the consumer observed the BODY value on the accessor (set by the inbound
            // filter from context.Message as ICorrelated — NOT the diverging envelope CorrelationId).
            Assert.Equal(bodyId.ToString(), ProbeConsumer.CapturedAccessorValue);
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    [Fact]
    public async Task Inbound_Filter_Tolerates_NonCorrelated_Message()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var provider = new ServiceCollection()
            .AddSingleton<ICorrelationAccessor, AsyncLocalCorrelationAccessor>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<PlainConsumer>();
                x.UsingInMemory((ctx, cfg) =>
                {
                    cfg.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx);     // CORR-01
                    cfg.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx);          // CORR-02
                    cfg.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);    // CORR-02
                    cfg.ConfigureEndpoints(ctx);
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // A message that does NOT implement ICorrelated must still be consumed without throwing
            // (where T : class preserved; `as ICorrelated` yields null → fresh-Guid fallback, no NRE).
            await harness.Bus.Publish(new PlainMessage("no correlation here"), ct);

            Assert.True(await harness.Consumed.Any<PlainMessage>(ct));
        }
        finally
        {
            await harness.Stop(ct);
        }
    }
}
