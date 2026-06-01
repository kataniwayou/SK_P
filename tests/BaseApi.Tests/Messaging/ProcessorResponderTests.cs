using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Repositories;
using BaseApi.Service;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Processor.Responders;
using FluentValidation;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

// NOTE: namespace is deliberately BaseApi.Tests.MessagingResponders (NOT BaseApi.Tests.Messaging) —
// see BaseApiCoreFirewallTests for why (a BaseApi.Tests.Messaging namespace would shadow the
// top-level Messaging namespace for sibling files referencing Messaging.Contracts.* unqualified).
namespace BaseApi.Tests.MessagingResponders;

/// <summary>
/// RPC-01 goal-backward proof of the <see cref="GetProcessorBySourceHashConsumer"/> dual-response
/// round-trip, driven by the in-memory MassTransit harness (mirror of <c>ResultConsumeTests</c>) over
/// the REAL <see cref="ProcessorService"/> against a seeded EF-InMemory <see cref="AppDbContext"/> —
/// no RabbitMQ broker. From the Phase-26 client's perspective:
/// <list type="bullet">
///   <item>a SEEDED source hash → the request client gets <see cref="ProcessorIdentityFound"/> with
///   the seeded Id + the three schema Ids;</item>
///   <item>an UNSEEDED hash → the client gets <see cref="ProcessorIdentityNotFound"/> echoing the
///   requested SourceHash (the backing read throws <c>NotFoundException</c>, caught by the consumer).</item>
/// </list>
/// </summary>
public sealed class ProcessorResponderTests
{
    private const string SeededHash =
        "abc1230000000000000000000000000000000000000000000000000000000000";   // lowercase 64-hex
    private const string MissingHash =
        "deadbeef00000000000000000000000000000000000000000000000000000000";

    private static readonly Guid SeededId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InputSchemaId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OutputSchemaId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ConfigSchemaId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>A real <see cref="AppDbContext"/> over EF-InMemory seeded with one Processor row.</summary>
    private static AppDbContext SeededDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"proc-responder-{Guid.NewGuid():N}")
            .Options;
        var db = new AppDbContext(options);
        db.Processors.Add(new ProcessorEntity
        {
            Id = SeededId,
            Name = "seed",
            Version = "1.0.0",
            SourceHash = SeededHash,
            InputSchemaId = InputSchemaId,
            OutputSchemaId = OutputSchemaId,
            ConfigSchemaId = ConfigSchemaId,
        });
        db.SaveChanges();
        return db;
    }

    /// <summary>The real ProcessorService — read path uses only the mapper + DbContext (validators unused).</summary>
    private static ProcessorService RealService(AppDbContext db)
    {
        IEntityMapper<ProcessorEntity, ProcessorCreateDto, ProcessorUpdateDto, ProcessorReadDto> mapper =
            new ProcessorEntityMapper();
        return new ProcessorService(
            Substitute.For<IValidator<ProcessorCreateDto>>(),
            Substitute.For<IValidator<ProcessorUpdateDto>>(),
            mapper,
            new Repository<ProcessorEntity>(db),
            db);
    }

    private static ServiceProvider BuildHarness(AppDbContext db) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(db)
            .AddSingleton(RealService(db))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<GetProcessorBySourceHashConsumer>();
                x.UsingInMemory((ctx, cfg) =>
                    cfg.ReceiveEndpoint(ProcessorQueues.IdentityQuery,
                        e => e.ConfigureConsumer<GetProcessorBySourceHashConsumer>(ctx)));
            })
            .BuildServiceProvider(true);

    [Fact]
    public async Task SeededHash_Responds_ProcessorIdentityFound_With_Seeded_Fields()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = SeededDb();
        await using var provider = BuildHarness(db);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var client = harness.GetRequestClient<GetProcessorBySourceHash>();
            var response = await client.GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>(
                new GetProcessorBySourceHash(SeededHash), ct);

            Assert.True(response.Is(out Response<ProcessorIdentityFound>? found));
            Assert.Equal(SeededId, found!.Message.Id);
            Assert.Equal(InputSchemaId, found.Message.InputSchemaId);
            Assert.Equal(OutputSchemaId, found.Message.OutputSchemaId);
            Assert.Equal(ConfigSchemaId, found.Message.ConfigSchemaId);
        }
        finally { await harness.Stop(ct); }
    }

    [Fact]
    public async Task UnseededHash_Responds_ProcessorIdentityNotFound_Echoing_SourceHash()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = SeededDb();
        await using var provider = BuildHarness(db);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var client = harness.GetRequestClient<GetProcessorBySourceHash>();
            var response = await client.GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>(
                new GetProcessorBySourceHash(MissingHash), ct);

            Assert.True(response.Is(out Response<ProcessorIdentityNotFound>? notFound));
            Assert.Equal(MissingHash, notFound!.Message.SourceHash);
        }
        finally { await harness.Stop(ct); }
    }
}
