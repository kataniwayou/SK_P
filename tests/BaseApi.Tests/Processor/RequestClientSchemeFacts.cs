using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Wave 0 confirmation gate (RESEARCH A2/A5, VALIDATION.md): proves the MEDIUM-confidence research
/// assumptions against a real in-memory responder BEFORE Plan 02 relies on them —
/// <list type="bullet">
///   <item>the <c>exchange:{name}</c> request-client URI scheme routes to the named WebApi responder
///   endpoint (<see cref="ProcessorQueues.IdentityQuery"/> / <see cref="ProcessorQueues.SchemaQuery"/>),</item>
///   <item>the dual-response <c>GetResponse&lt;TFound, TNotFound&gt;(message, ct, RequestTimeout.After(...))</c>
///   overload + <c>response.Is(out Response&lt;T&gt;)</c> API resolve as researched.</item>
/// </list>
/// Both queries are confirmed (identity + schema). The harness responders are set to reply Found
/// immediately. A short cancellation timeout fails a routing failure fast rather than hanging.
/// </summary>
public sealed class RequestClientSchemeFacts
{
    [Fact]
    public async Task Exchange_Scheme_Routes_And_Dual_Response_Overload_Resolves()
    {
        // Found immediately (NotFound budget = 0) — this fact confirms the routing + overload, not the sequence.
        var sequence = new ProcessorTestHarness.ResponderSequence
        {
            IdentityNotFoundCount = 0,
            SchemaNotFoundCount = 0,
        };

        await using var provider = ProcessorTestHarness.BuildProvider(sequence);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        // Fail fast on a routing failure rather than hanging the suite.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        try
        {
            // IRequestClient<T> is a SCOPED service — resolve it from a DI scope, not the root
            // provider (MassTransit registers per-message-type request clients as scoped).
            using var scope = provider.CreateScope();
            var sp = scope.ServiceProvider;

            // ---- Identity query (RPC-01): exchange: scheme + GetResponse<TFound,TNotFound> ----
            var identityClient = sp.GetRequiredService<IRequestClient<GetProcessorBySourceHash>>();
            // 8.5.5: the dual GetResponse<T1,T2> overload returns Response<T1,T2> (NOT the base
            // Response) — .Is(out Response<T>) is declared on the dual-response type. (Wave 0 A5 correction.)
            Response<ProcessorIdentityFound, ProcessorIdentityNotFound> identityResponse = await identityClient
                .GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>(
                    new GetProcessorBySourceHash("deadbeef"), ct, RequestTimeout.After(s: 5));

            Assert.True(identityResponse.Is(out Response<ProcessorIdentityFound>? found));
            Assert.Equal(sequence.FoundProcessorId, found!.Message.Id);

            // ---- Schema query (RPC-02): same scheme + overload, second contract ----
            var schemaClient = sp.GetRequiredService<IRequestClient<GetSchemaDefinition>>();
            Response<SchemaDefinitionFound, SchemaDefinitionNotFound> schemaResponse = await schemaClient
                .GetResponse<SchemaDefinitionFound, SchemaDefinitionNotFound>(
                    new GetSchemaDefinition(Guid.NewGuid()), ct, RequestTimeout.After(s: 5));

            Assert.True(schemaResponse.Is(out Response<SchemaDefinitionFound>? def));
            Assert.Equal(sequence.FoundDefinition, def!.Message.Definition);
        }
        finally
        {
            await harness.Stop(TestContext.Current.CancellationToken);
        }
    }
}
