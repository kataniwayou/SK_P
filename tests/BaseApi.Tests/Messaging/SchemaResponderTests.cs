using BaseApi.Core.Mapping;
using BaseApi.Core.Persistence.Repositories;
using BaseApi.Service;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Schema.Responders;
using FluentValidation;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

// NOTE: namespace BaseApi.Tests.MessagingResponders (NOT BaseApi.Tests.Messaging) — see
// BaseApiCoreFirewallTests for the shadowing rationale.
namespace BaseApi.Tests.MessagingResponders;

/// <summary>
/// RPC-02 goal-backward proof of the <see cref="GetSchemaDefinitionConsumer"/> dual-response round-trip,
/// driven by the in-memory MassTransit harness over the REAL <see cref="SchemaService"/> against a
/// seeded EF-InMemory <see cref="AppDbContext"/> — no RabbitMQ broker. From the client's perspective:
/// <list type="bullet">
///   <item>a SEEDED schema Id → <see cref="SchemaDefinitionFound"/> carrying the seeded Definition;</item>
///   <item>an UNSEEDED Id → <see cref="SchemaDefinitionNotFound"/> echoing the requested SchemaId
///   (the inherited <c>BaseService.GetByIdAsync</c> throws <c>NotFoundException</c>, caught by the consumer).</item>
/// </list>
/// </summary>
public sealed class SchemaResponderTests
{
    private static readonly Guid SeededId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MissingId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string SeededDefinition = "{\"type\":\"object\"}";

    private static AppDbContext SeededDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"schema-responder-{Guid.NewGuid():N}")
            .Options;
        var db = new AppDbContext(options);
        db.Schemas.Add(new SchemaEntity
        {
            Id = SeededId,
            Name = "seed",
            Version = "1.0.0",
            Definition = SeededDefinition,
        });
        db.SaveChanges();
        return db;
    }

    private static SchemaService RealService(AppDbContext db)
    {
        IEntityMapper<SchemaEntity, SchemaCreateDto, SchemaUpdateDto, SchemaReadDto> mapper =
            new SchemaEntityMapper();
        return new SchemaService(
            Substitute.For<IValidator<SchemaCreateDto>>(),
            Substitute.For<IValidator<SchemaUpdateDto>>(),
            mapper,
            new Repository<SchemaEntity>(db),
            db);
    }

    private static ServiceProvider BuildHarness(AppDbContext db) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(db)
            .AddSingleton(RealService(db))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<GetSchemaDefinitionConsumer>();
                x.UsingInMemory((ctx, cfg) =>
                    cfg.ReceiveEndpoint(ProcessorQueues.SchemaQuery,
                        e => e.ConfigureConsumer<GetSchemaDefinitionConsumer>(ctx)));
            })
            .BuildServiceProvider(true);

    [Fact]
    public async Task SeededId_Responds_SchemaDefinitionFound_With_Seeded_Definition()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = SeededDb();
        await using var provider = BuildHarness(db);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var client = harness.GetRequestClient<GetSchemaDefinition>();
            var response = await client.GetResponse<SchemaDefinitionFound, SchemaDefinitionNotFound>(
                new GetSchemaDefinition(SeededId), ct);

            Assert.True(response.Is(out Response<SchemaDefinitionFound>? found));
            Assert.Equal(SeededDefinition, found!.Message.Definition);
        }
        finally { await harness.Stop(ct); }
    }

    [Fact]
    public async Task UnseededId_Responds_SchemaDefinitionNotFound_Echoing_SchemaId()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = SeededDb();
        await using var provider = BuildHarness(db);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var client = harness.GetRequestClient<GetSchemaDefinition>();
            var response = await client.GetResponse<SchemaDefinitionFound, SchemaDefinitionNotFound>(
                new GetSchemaDefinition(MissingId), ct);

            Assert.True(response.Is(out Response<SchemaDefinitionNotFound>? notFound));
            Assert.Equal(MissingId, notFound!.Message.SchemaId);
        }
        finally { await harness.Stop(ct); }
    }
}
