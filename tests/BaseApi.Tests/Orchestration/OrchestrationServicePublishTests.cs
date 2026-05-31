using BaseApi.Core.Exceptions.Handlers;
using BaseApi.Core.Persistence;
using BaseApi.Service;
using BaseApi.Service.Features.Orchestration;
using BaseApi.Service.Features.Orchestration.Loading;
using BaseApi.Service.Features.Orchestration.Projection;
using BaseApi.Service.Features.Orchestration.Validation;
using BaseApi.Service.Features.Workflow;
using FluentValidation;
using FluentValidation.Results;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestration;

/// <summary>
/// In-memory harness proof for the WebApi publish boundary (Phase 19 is harness-only — the
/// real-broker-down end-to-end HTTP path is Phase 20, TEST-RMQ-03). Drives the REAL
/// <see cref="OrchestrationService"/> against the harness's <see cref="IPublishEndpoint"/> with a
/// seeded EF-InMemory <see cref="AppDbContext"/> and stubbed L2 seams, so it proves the ACTUAL
/// publish call site.
/// <list type="bullet">
///   <item>MSG-WEBAPI-02: StartAsync over a present workflow → <c>harness.Published.Any&lt;StartOrchestration&gt;()</c>
///         true; the published body carries the input WorkflowIds + a non-empty (NewId) CorrelationId.</item>
///   <item>MSG-WEBAPI-02: StopAsync (gate passes) → <c>harness.Published.Any&lt;StopOrchestration&gt;()</c> true.</item>
///   <item>D-02: the publish call stamps correlation on the BODY only — no divergent envelope id
///         (MassTransit by-convention populates the envelope EQUAL to the body value; single source of truth).</item>
///   <item>MSG-WEBAPI-03 (failure path, service boundary): a faulting IPublishEndpoint makes
///         StartAsync PROPAGATE the exception (not swallowed) — broker is a hard dep for Start;
///         and <see cref="FallbackExceptionHandler"/> maps an unhandled exception to 500 + ProblemDetails.</item>
/// </list>
/// </summary>
public sealed class OrchestrationServicePublishTests
{
    // ----- collaborators ------------------------------------------------------------------------

    /// <summary>A real <see cref="AppDbContext"/> over EF-InMemory seeded with the given workflow ids.</summary>
    private static AppDbContext SeededDb(params Guid[] workflowIds)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"orch-publish-{Guid.NewGuid():N}")
            .Options;
        var db = new AppDbContext(options);
        foreach (var id in workflowIds)
        {
            db.Workflows.Add(new WorkflowEntity { Id = id, CronExpression = null });
        }
        db.SaveChanges();
        return db;
    }

    /// <summary>An ids-validator stub that always passes (the existence + rule gate is not under test here).</summary>
    private static IValidator<IReadOnlyList<Guid>> PassingValidator()
    {
        var v = Substitute.For<IValidator<IReadOnlyList<Guid>>>();
        v.ValidateAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new FluentValidation.Results.ValidationResult());
        return v;
    }

    /// <summary>A loader stub returning an EMPTY snapshot (passes all three validators — no cycles/edges/payloads).</summary>
    private static IWorkflowGraphLoader EmptySnapshotLoader()
    {
        var loader = Substitute.For<IWorkflowGraphLoader>();
        loader.LoadL1Async(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => new WorkflowGraphSnapshot(NullLogger<WorkflowGraphSnapshot>.Instance));
        return loader;
    }

    /// <summary>A multiplexer whose KeyExistsAsync returns true for every key (Stop gate passes).</summary>
    private static IConnectionMultiplexer AllKeysExist()
    {
        var db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
        return mux;
    }

    private static IHttpContextAccessor NoHttpContext()
    {
        var acc = Substitute.For<IHttpContextAccessor>();
        acc.HttpContext.Returns((HttpContext?)null);
        return acc;
    }

    /// <summary>Builds the REAL OrchestrationService with stubbed seams against the supplied publish endpoint.</summary>
    private static OrchestrationService BuildService(
        BaseDbContext db, IPublishEndpoint publishEndpoint, IConnectionMultiplexer? mux = null)
    {
        // Phase 22 (Plan 04) added the async ProcessorLivenessValidator after the sync trio. The
        // EmptySnapshotLoader yields ZERO processors, so the validator's per-processor GET loop never
        // runs — the same mux + TimeProvider.System satisfy its ctor without affecting the publish path.
        var liveMux = mux ?? AllKeysExist();
        return new OrchestrationService(
            db,
            PassingValidator(),
            EmptySnapshotLoader(),
            new CycleDetector(),
            new SchemaEdgeValidator(),
            new PayloadConfigSchemaValidator(),
            new ProcessorLivenessValidator(liveMux, TimeProvider.System),
            Substitute.For<IRedisProjectionWriter>(),   // no-op upsert
            Substitute.For<IRedisL2Cleanup>(),          // no-op cleanup
            NoHttpContext(),
            liveMux,
            publishEndpoint,
            NullLogger<OrchestrationService>.Instance);
    }

    // ----- MSG-WEBAPI-02: Start publishes StartOrchestration with body CorrelationId -------------

    [Fact]
    public async Task StartAsync_Publishes_StartOrchestration_With_Input_Ids_And_Body_CorrelationId()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();

        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            using var db = SeededDb(workflowId);
            var svc = BuildService(db, harness.Bus);

            await svc.StartAsync(new[] { workflowId }, ct);

            // MSG-WEBAPI-02: a StartOrchestration was published.
            Assert.True(await harness.Published.Any<StartOrchestration>(ct));

            var published = harness.Published.Select<StartOrchestration>(ct).Single();
            // Body carries the input WorkflowIds.
            Assert.Equal(new[] { workflowId }, published.Context.Message.WorkflowIds);
            // Body CorrelationId is set (NewId, never Guid.Empty).
            Assert.NotEqual(Guid.Empty, published.Context.Message.CorrelationId);
            // D-02 / T-19-envelope-leak: the publish call sets correlation on the BODY ONLY — it
            // never stamps a separate/divergent envelope id. MassTransit auto-populates the envelope
            // CorrelationId BY CONVENTION from the message's CorrelationId property (the 19-01 masking
            // effect), so the single source of truth holds: the envelope id, when present, EQUALS the
            // body id — it is never a different value the way an explicit envelope stamp would be.
            Assert.Equal(published.Context.Message.CorrelationId, published.Context.CorrelationId);
        }
        finally { await harness.Stop(ct); }
    }

    // ----- MSG-WEBAPI-02: Stop publishes StopOrchestration ---------------------------------------

    [Fact]
    public async Task StopAsync_Publishes_StopOrchestration_With_Body_CorrelationId()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();

        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            using var db = SeededDb(workflowId);
            var svc = BuildService(db, harness.Bus);

            await svc.StopAsync(new[] { workflowId }, ct);

            Assert.True(await harness.Published.Any<StopOrchestration>(ct));

            var published = harness.Published.Select<StopOrchestration>(ct).Single();
            Assert.Equal(new[] { workflowId }, published.Context.Message.WorkflowIds);
            Assert.NotEqual(Guid.Empty, published.Context.Message.CorrelationId);
            // body-only (D-02): no divergent envelope stamp — envelope id equals the body id
            // (MassTransit by-convention population, the 19-01 masking effect).
            Assert.Equal(published.Context.Message.CorrelationId, published.Context.CorrelationId);
        }
        finally { await harness.Stop(ct); }
    }

    // ----- MSG-WEBAPI-03: faulting publisher propagates out of StartAsync ------------------------

    [Fact]
    public async Task StartAsync_Propagates_Publish_Failure_Broker_Hard_Dep()
    {
        var ct = TestContext.Current.CancellationToken;
        var workflowId = Guid.NewGuid();

        // A faulting publish endpoint (simulates broker-unreachable at publish time).
        var faulting = Substitute.For<IPublishEndpoint>();
        faulting.Publish(Arg.Any<StartOrchestration>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new Exception("broker unreachable")));

        using var db = SeededDb(workflowId);
        var svc = BuildService(db, faulting);

        // MSG-WEBAPI-03: the publish failure PROPAGATES out of StartAsync (not swallowed) —
        // the broker is a hard dependency for the Start path.
        await Assert.ThrowsAnyAsync<Exception>(() => svc.StartAsync(new[] { workflowId }, ct));
    }

    // ----- MSG-WEBAPI-03: FallbackExceptionHandler maps an unhandled exception to 500 ------------

    [Fact]
    public async Task FallbackExceptionHandler_Maps_Unhandled_Exception_To_500_ProblemDetails()
    {
        var ct = TestContext.Current.CancellationToken;

        // Framework ProblemDetails service (writes the RFC 7807 body).
        var provider = new ServiceCollection()
            .AddLogging()
            .AddProblemDetails()
            .BuildServiceProvider();
        var pdSvc = provider.GetRequiredService<IProblemDetailsService>();

        var handler = new FallbackExceptionHandler(
            pdSvc, NullLogger<FallbackExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        httpContext.RequestServices = provider;

        // An arbitrary unhandled exception (a publish failure surfaces here as the catch-all).
        var handled = await handler.TryHandleAsync(
            httpContext, new Exception("broker unreachable"), ct);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
    }
}
