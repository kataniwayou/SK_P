using System.Linq;
using BaseConsole.Core.Messaging;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// CORR-03: the CONSOLE outbound publish filter
/// (<see cref="OutboundCorrelationPublishFilter{T}"/>) stamps the ambient correlation id from
/// <see cref="ICorrelationAccessor"/> onto the published message ENVELOPE
/// (<c>context.CorrelationId</c>) — proven by a synthetic in-memory send with NO real downstream
/// consumer.
/// <para>
/// This is DISTINCT from the WebApi path, which sets the message BODY and has no outbound filter
/// (D-01/D-02). The filter stamps the envelope, never the body — keep the two mechanics separate.
/// </para>
/// </summary>
public sealed class OutboundFilterSyntheticTests
{
    [Fact]
    public async Task Outbound_Filter_Stamps_Ambient_Id_On_Published_Envelope()
    {
        var ct = TestContext.Current.CancellationToken;

        var ambient = new AsyncLocalCorrelationAccessor();
        var stampId = NewId.NextGuid();
        ambient.Set(stampId.ToString());

        await using var provider = new ServiceCollection()
            .AddSingleton<ICorrelationAccessor>(ambient)
            .AddMassTransitTestHarness(x =>
                x.UsingInMemory((c, cfg) =>
                {
                    // The filter under test — the DI-aware generic registration form, taking the
                    // bus registration context so OutboundCorrelationPublishFilter<T>'s
                    // ICorrelationAccessor ctor dependency resolves from the container.
                    cfg.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), c);
                    cfg.ConfigureEndpoints(c);
                }))
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            // Body CorrelationId left default (ICorrelated is get-only; the body is never mutated).
            await harness.Bus.Publish(new StartOrchestration([Guid.NewGuid()]), ct);

            var pub = harness.Published.Select<StartOrchestration>(ct).Single();

            // CORR-03: the OUTBOUND filter stamped the ambient id onto the ENVELOPE.
            // Assert the ENVELOPE (pub.Context.CorrelationId), NOT the body
            // (pub.Context.Message.CorrelationId) — the filter touches only the envelope.
            Assert.Equal(stampId, pub.Context.CorrelationId);
        }
        finally { await harness.Stop(ct); }
    }
}
