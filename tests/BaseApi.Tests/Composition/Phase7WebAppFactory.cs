using BaseApi.Core.DependencyInjection;
using BaseApi.Core.Persistence;
using BaseApi.Core.Persistence.Interceptors;
using BaseApi.Core.Services;
using BaseApi.Tests.Middleware;
using BaseApi.Tests.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

// Two PostgresFixture classes coexist (BaseApi.Tests.Middleware vs BaseApi.Tests.Persistence).
// Alias the Persistence-flavored one — its `stepsdb_test_` DB-name prefix is what Plan 03-02's
// snapshot discipline tracks.
using PostgresFixture = BaseApi.Tests.Persistence.PostgresFixture;

namespace BaseApi.Tests.Composition;

/// <summary>
/// WebAppFactory subclass for Phase 7 facts. Adds:
/// (a) (removed IN-07 — base WebAppFactory.ConfigureWebHost already registers this assembly's
///     application part, so TestsController is discovered without an additional call here);
/// (b) <c>AddBaseApiValidation</c> + <c>AddBaseApiMapping</c> scanning the Tests assembly so
///     TestDtoValidator (TestUpdateDto), TestCreateDtoValidator (Blocker 2 fix), and TestEntityMapper
///     are visible to AddBaseApi's production scan (Phase 6 D-16 multi-assembly pattern — RESEARCH Pitfall 8);
/// (c) <c>AddScoped&lt;RecordingTestService&gt;()</c> AND alias
///     <c>AddScoped&lt;BaseService&lt;TestEntity,TestCreateDto,TestUpdateDto,TestReadDto&gt;&gt;(sp =&gt; sp.GetRequiredService&lt;RecordingTestService&gt;())</c>
///     — the alias is LOAD-BEARING because TestsController.ctor injects the abstract BaseService
///     (Warning 7 option b). Without the alias, DI cannot resolve the controller's dependency.
///
/// <para>
/// <b>Deviation [Rule 3 - Blocking] (Plan 07-02 Task 2 — surfaced by first verification run 2026-05-27):</b>
/// AppDbContext is a Phase 7 placeholder with NO DbSets and NO entity model entries — calling
/// <c>db.Set&lt;TestEntity&gt;()</c> through it would fail at runtime because TestEntity is not
/// registered in the model. Plan body assumed AppDbContext could host TestEntity, but the
/// composition root + Phase7TestDbContext design treats them as two separate worlds. Fix-forward:
/// override the <see cref="BaseDbContext"/> alias inside <c>ConfigureTestServices</c> to resolve
/// to a per-class <see cref="Phase7TestDbContext"/> (which DOES expose
/// <c>DbSet&lt;TestEntity&gt;</c>) bound to a throwaway Postgres DB via <see cref="PostgresFixture"/>.
/// The AppDbContext registration from <see cref="BaseApiServiceCollectionExtensions.AddBaseApi"/>
/// stays so AddBaseApiFacts (which asserts the alias graph for the production composition) keeps
/// working — but for integration tests that hit <c>/api/v1/tests</c>, every <see cref="Repository{T}"/>
/// resolves <see cref="BaseDbContext"/> = <see cref="Phase7TestDbContext"/>.
/// </para>
/// </summary>
public class Phase7WebAppFactory : WebAppFactory, IAsyncLifetime
{
    private PostgresFixture? _fixture;

    public async ValueTask InitializeAsync()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        // Create the schema in the throwaway DB so /api/v1/tests probes succeed.
        // EnsureCreatedAsync uses the model wired in Phase7TestDbContext.OnModelCreating
        // (inherited BaseDbContext xmin shadow token + snake_case naming convention).
        var opts = new DbContextOptionsBuilder<Phase7TestDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var ctx = new Phase7TestDbContext(opts);
        await ctx.Database.EnsureCreatedAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_fixture is not null)
        {
            // WR-05: clear only THIS factory's connection pool, not every Npgsql pool in-process,
            // so parallel xUnit v3 test classes don't lose their pooled connections to
            // NpgsqlConnection.ClearAllPools() (which is process-global). The connection here
            // is created only to identify the pool — it's never opened.
            await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
                NpgsqlConnection.ClearPool(conn);
            await _fixture.DisposeAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Override the connection string via in-memory configuration BEFORE base.ConfigureWebHost
        // runs (Program.cs reads `ConnectionStrings:Postgres` via IConfiguration when AddBaseApi
        // wires the DbContext). The throwaway DB is created in InitializeAsync above; reading
        // _fixture!.ConnectionString here would race with the WebApplicationFactory lazy boot,
        // but xUnit invokes InitializeAsync BEFORE the first test method (which is BEFORE the
        // first CreateClient/Services access — when ConfigureWebHost actually runs). Safe.
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _fixture!.ConnectionString,
            });
        });

        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            // IN-07: the base WebAppFactory.ConfigureWebHost already registers this assembly
            // via AddApplicationPart; ApplicationPartManager dedupes by reference equality so
            // a duplicate call is a no-op. Removed to avoid misleading future readers.
            services.AddBaseApiValidation(typeof(Phase7WebAppFactory).Assembly);
            services.AddBaseApiMapping(typeof(Phase7WebAppFactory).Assembly);

            // [Rule 3 - Blocking] Wire Phase7TestDbContext (which has DbSet<TestEntity>) +
            // override BaseDbContext alias so Repository<TestEntity>'s ctor receives a DbContext
            // whose model includes TestEntity. AppDbContext registration from AddBaseApi is left
            // in place so AddBaseApiFacts' DI graph assertions still work; integration tests use
            // the BaseDbContext alias (which Repository<T> depends on).
            services.AddDbContext<Phase7TestDbContext>((sp, opts) =>
            {
                opts.UseNpgsql(_fixture!.ConnectionString)
                    .UseSnakeCaseNamingConvention()
                    .AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
            });

            // Remove the previously registered BaseDbContext alias (originally points at AppDbContext)
            // and re-register it pointing at Phase7TestDbContext.
            var existing = services.Where(sd => sd.ServiceType == typeof(BaseDbContext)).ToList();
            foreach (var sd in existing) services.Remove(sd);
            services.AddScoped<BaseDbContext>(sp => sp.GetRequiredService<Phase7TestDbContext>());

            // Pitfall 10 + Warning 7 option b: register concrete recording service + LOAD-BEARING
            // alias (TestsController injects the abstract base).
            services.AddScoped<RecordingTestService>();
            services.AddScoped<BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>>(
                sp => sp.GetRequiredService<RecordingTestService>());
        });
    }
}
