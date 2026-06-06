using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Wave 0 reusable in-memory MassTransit harness for the Processor test slice. It stands up the two
/// WebApi responders as SEQUENCEABLE consumers bound on the NAMED receive endpoints
/// <see cref="ProcessorQueues.IdentityQuery"/> and <see cref="ProcessorQueues.SchemaQuery"/>, and
/// registers the two request clients exactly as the production composition will — targeting
/// <c>exchange:{name}</c> so the request client's exchange routing resolves to the named endpoint.
///
/// <para>
/// The identity/schema responders are sequenceable via a shared <see cref="ResponderSequence"/>:
/// the first N replies are NotFound, then Found thereafter (the Wave 0
/// NotFound-&gt;NotFound-&gt;Found fixture Plan 02 reuses to prove retry-then-resolve). Set the
/// threshold to 0 to respond Found immediately (the RequestClientSchemeFacts confirmation case).
/// </para>
/// </summary>
public static class ProcessorTestHarness
{
    /// <summary>
    /// Controls how many leading replies are NotFound before the responder switches to Found.
    /// Shared singleton injected into both responders; the test mutates the thresholds.
    /// </summary>
    public sealed class ResponderSequence
    {
        private int _identityNotFoundCalls;
        private int _schemaNotFoundCalls;

        /// <summary>How many leading identity replies are NotFound (then Found). Default 0 = Found immediately.</summary>
        public int IdentityNotFoundCount { get; set; }

        /// <summary>How many leading schema replies are NotFound (then Found). Default 0 = Found immediately.</summary>
        public int SchemaNotFoundCount { get; set; }

        /// <summary>The Id the identity responder returns once it switches to Found.</summary>
        public Guid FoundProcessorId { get; set; } = Guid.NewGuid();

        /// <summary>The definition the schema responder returns once it switches to Found.</summary>
        public string FoundDefinition { get; set; } = "{\"type\":\"object\"}";

        /// <summary>True for the call that should reply NotFound; false once the NotFound budget is spent.</summary>
        public bool NextIdentityIsNotFound() =>
            Interlocked.Increment(ref _identityNotFoundCalls) <= IdentityNotFoundCount;

        /// <summary>True for the call that should reply NotFound; false once the NotFound budget is spent.</summary>
        public bool NextSchemaIsNotFound() =>
            Interlocked.Increment(ref _schemaNotFoundCalls) <= SchemaNotFoundCount;
    }

    /// <summary>Sequenceable identity responder: NotFound for the first N calls, then Found (D-04 dual-response).</summary>
    public sealed class IdentityResponder(ResponderSequence sequence) : IConsumer<GetProcessorBySourceHash>
    {
        public async Task Consume(ConsumeContext<GetProcessorBySourceHash> context)
        {
            if (sequence.NextIdentityIsNotFound())
                await context.RespondAsync(new ProcessorIdentityNotFound(context.Message.SourceHash));
            else
                await context.RespondAsync(new ProcessorIdentityFound(
                    sequence.FoundProcessorId, InputSchemaId: null, OutputSchemaId: null, ConfigSchemaId: null,
                    Name: "proc", Version: "1.0.0"));
        }
    }

    /// <summary>Sequenceable schema responder: NotFound for the first N calls, then Found (D-04 dual-response).</summary>
    public sealed class SchemaResponder(ResponderSequence sequence) : IConsumer<GetSchemaDefinition>
    {
        public async Task Consume(ConsumeContext<GetSchemaDefinition> context)
        {
            if (sequence.NextSchemaIsNotFound())
                await context.RespondAsync(new SchemaDefinitionNotFound(context.Message.SchemaId));
            else
                await context.RespondAsync(new SchemaDefinitionFound(sequence.FoundDefinition));
        }
    }

    /// <summary>
    /// Builds an in-memory harness provider: the two sequenceable responders bound on the named
    /// endpoints + the two request clients targeting <c>exchange:{name}</c>. Caller starts/stops the
    /// resolved <see cref="ITestHarness"/>.
    /// </summary>
    public static ServiceProvider BuildProvider(ResponderSequence sequence)
        => new ServiceCollection()
            .AddSingleton(sequence)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<IdentityResponder>();
                x.AddConsumer<SchemaResponder>();

                // Request clients targeting the Phase 25 named responder endpoints (RPC-04). The
                // exchange: scheme is the Wave 0 confirmation target (RESEARCH A2/A5).
                x.AddRequestClient<GetProcessorBySourceHash>(new Uri("exchange:" + ProcessorQueues.IdentityQuery));
                x.AddRequestClient<GetSchemaDefinition>(new Uri("exchange:" + ProcessorQueues.SchemaQuery));

                x.UsingInMemory((ctx, cfg) =>
                {
                    // Bind each responder on its NAMED receive endpoint so exchange:{name} routes to it
                    // (mirrors ResponderMessaging.cs — NO ConfigureEndpoints auto-naming).
                    cfg.ReceiveEndpoint(ProcessorQueues.IdentityQuery,
                        e => e.ConfigureConsumer<IdentityResponder>(ctx));
                    cfg.ReceiveEndpoint(ProcessorQueues.SchemaQuery,
                        e => e.ConfigureConsumer<SchemaResponder>(ctx));
                });
            })
            .BuildServiceProvider(true);
}
