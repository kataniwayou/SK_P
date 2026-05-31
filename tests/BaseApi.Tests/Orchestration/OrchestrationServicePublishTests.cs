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
/// In-memory harness proof for the WebApi publish boundary, reconciled to the Phase 24 first-win
/// semantics (WEBAPI-SUPPRESS-01 / D-04). Drives the REAL <see cref="OrchestrationService"/> against
/// the harness's <see cref="IPublishEndpoint"/> with a seeded EF-InMemory <see cref="AppDbContext"/>
/// and stubbed L2 seams, so it proves the ACTUAL publish call site.
/// <list type="bullet">
///   <item>WEBAPI-SUPPRESS-01: Start over an ABSENT root → StartOrchestration published carrying the
///         newly-written id; over a PRESENT root → first-win skip, NO publish.</item>
///   <item>WEBAPI-SUPPRESS-01: a mixed present+absent Start publishes ONLY the absent (written) ids.</item>
///   <item>WEBAPI-SUPPRESS-01: Stop over a PRESENT root (KeyDeleteAsync true) → StopOrchestration
///         published for it; over an ABSENT root (KeyDeleteAsync false) → no-op, NO publish.</item>
///   <item>D-02: the publish call stamps correlation on the BODY only — no divergent envelope id
///         (MassTransit by-convention populates the envelope EQUAL to the body value).</item>
///   <item>MSG-WEBAPI-03 (failure path): a faulting IPublishEndpoint makes StartAsync PROPAGATE the
///         exception; FallbackExceptionHandler maps an unhandled exception to 500 + ProblemDetails.</item>
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

    /// <summary>
    /// A multiplexer whose Start first-win probe (<c>KeyExistsAsync</c>) reports the given roots as
    /// PRESENT and all others as absent, and whose Stop <c>KeyDeleteAsync</c> reports a delete for the
    /// SAME present-roots set (false for absent ids). The root key is <c>{prefix}{id:D}</c>, so we
    /// match on the rendered id substring (prefix-agnostic).
    /// </summary>
    private static IConnectionMultiplexer MuxWithPresentRoots(params Guid[] presentRoots)
    {
        var present = new HashSet<string>(presentRoots.Select(id => id.ToString("D")));
        bool Matches(RedisKey k) => present.Any(id => ((string)k!).Contains(id));

        var db = Substitute.For<IDatabase>();
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => Matches(ci.Arg<RedisKey>()));
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => Matches(ci.Arg<RedisKey>()));
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
        // The EmptySnapshotLoader yields ZERO processors, so the ProcessorLivenessValidator's
        // per-processor GET loop never runs — the same mux + TimeProvider.System satisfy its ctor
        // without affecting the publish path. Default mux: no present roots (every Start writes).
        var liveMux = mux ?? MuxWithPresentRoots();
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

    // ----- WEBAPI-SUPPRESS-01: Start over an ABSENT root publishes the written id ----------------

    [Fact]
    public async Task StartAsync_AbsentRoot_Publishes_StartOrchestration_With_Written_Id_And_Body_CorrelationId()
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
            // No present roots → the Start first-win probe writes + publishes.
            var svc = BuildService(db, harness.Bus, MuxWithPresentRoots());

            await svc.StartAsync(new[] { workflowId }, ct);

            Assert.True(await harness.Published.Any<StartOrchestration>(ct));

            var published = harness.Published.Select<StartOrchestration>(ct).Single();
            // Body carries the newly-written id (the deduped subset = the single absent id here).
            Assert.Equal(new[] { workflowId }, published.Context.Message.WorkflowIds);
            // Body CorrelationId is set (NewId, never Guid.Empty).
            Assert.NotEqual(Guid.Empty, published.Context.Message.CorrelationId);
            // D-02 / T-19-envelope-leak: correlation on the BODY only; the envelope id (when present)
            // EQUALS the body id (MassTransit by-convention population).
            Assert.Equal(published.Context.Message.CorrelationId, published.Context.CorrelationId);
        }
        finally { await harness.Stop(ct); }
    }

    // ----- WEBAPI-SUPPRESS-01: all-duplicate Start publishes NOTHING (first-win skip) ------------

    [Fact]
    public async Task StartAsync_AllPresentRoots_Publishes_Nothing()
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
            // The root is already present → first-win skip → NO StartOrchestration published.
            var svc = BuildService(db, harness.Bus, MuxWithPresentRoots(workflowId));

            await svc.StartAsync(new[] { workflowId }, ct);

            Assert.False(await harness.Published.Any<StartOrchestration>(ct));
        }
        finally { await harness.Stop(ct); }
    }

    // ----- WEBAPI-SUPPRESS-01: mixed Start publishes ONLY the absent (written) ids ---------------

    [Fact]
    public async Task StartAsync_Mixed_Publishes_Only_Absent_Ids()
    {
        var ct = TestContext.Current.CancellationToken;
        var presentId = Guid.NewGuid();
        var absentId = Guid.NewGuid();

        await using var provider = new ServiceCollection()
            .AddMassTransitTestHarness()
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            using var db = SeededDb(presentId, absentId);
            // presentId root exists (skipped), absentId root absent (written).
            var svc = BuildService(db, harness.Bus, MuxWithPresentRoots(presentId));

            await svc.StartAsync(new[] { presentId, absentId }, ct);

            Assert.True(await harness.Published.Any<StartOrchestration>(ct));
            var published = harness.Published.Select<StartOrchestration>(ct).Single();
            // Deduped subset: ONLY the absent id is published, never the present (duplicate) one.
            Assert.Equal(new[] { absentId }, published.Context.Message.WorkflowIds);
        }
        finally { await harness.Stop(ct); }
    }

    // ----- WEBAPI-SUPPRESS-01: Stop over a PRESENT root publishes StopOrchestration --------------

    [Fact]
    public async Task StopAsync_PresentRoot_Publishes_StopOrchestration_With_Body_CorrelationId()
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
            // Root present → KeyDeleteAsync returns true → deleted + published.
            var svc = BuildService(db, harness.Bus, MuxWithPresentRoots(workflowId));

            await svc.StopAsync(new[] { workflowId }, ct);

            Assert.True(await harness.Published.Any<StopOrchestration>(ct));

            var published = harness.Published.Select<StopOrchestration>(ct).Single();
            Assert.Equal(new[] { workflowId }, published.Context.Message.WorkflowIds);
            Assert.NotEqual(Guid.Empty, published.Context.Message.CorrelationId);
            Assert.Equal(published.Context.Message.CorrelationId, published.Context.CorrelationId);
        }
        finally { await harness.Stop(ct); }
    }

    // ----- WEBAPI-SUPPRESS-01: Stop over an ABSENT root is a no-op (publishes NOTHING) -----------

    [Fact]
    public async Task StopAsync_AbsentRoot_Is_NoOp_Publishes_Nothing()
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
            // No present roots → KeyDeleteAsync returns false → no-op → NO StopOrchestration.
            var svc = BuildService(db, harness.Bus, MuxWithPresentRoots());

            await svc.StopAsync(new[] { workflowId }, ct);

            Assert.False(await harness.Published.Any<StopOrchestration>(ct));
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
        // Absent root → Start proceeds to the publish (which faults).
        var svc = BuildService(db, faulting, MuxWithPresentRoots());

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
