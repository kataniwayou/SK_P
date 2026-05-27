using BaseApi.Tests.Middleware;
using BaseApi.Tests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

// Two PostgresFixture classes coexist (BaseApi.Tests.Middleware vs BaseApi.Tests.Persistence).
// Alias the Persistence-flavored one — its `stepsdb_test_` DB-name prefix is what the
// Phase 3 D-15 snapshot discipline tracks (matches Phase7WebAppFactory's alias).
using PostgresFixture = BaseApi.Tests.Persistence.PostgresFixture;

namespace BaseApi.Tests.Composition;

/// <summary>
/// Phase 8 composition root for integration tests against the real production
/// AppDbContext + the 5 concrete entity controllers. Encapsulates PostgresFixture
/// internally because xUnit cannot order fixture instantiation between two
/// IClassFixture&lt;&gt; slots (Phase 7 D-08 / Plan 05-02 Pattern C).
/// <para>
/// Unlike Phase7WebAppFactory, this factory does NOT rewire AppDbContext: Wave C
/// 08-07 populates AppDbContext with real DbSets so the abstract Repository&lt;TEntity&gt;
/// binds naturally via the BaseDbContext alias registered by AddBaseApiPersistence.
/// </para>
/// </summary>
public class Phase8WebAppFactory : WebAppFactory, IAsyncLifetime
{
    private PostgresFixture? _fixture;
    private readonly string? _connectionStringOverride;

    public Phase8WebAppFactory() { }

    /// <summary>
    /// Constructor used by Plan 08-08 MigrationFailureWebAppFactory subclass to inject
    /// a deliberately bad connection string (e.g., wrong port 5434) so MigrateAsync
    /// throws at startup and D-16 behavior can be asserted.
    /// </summary>
    protected Phase8WebAppFactory(string connectionStringOverride)
    {
        _connectionStringOverride = connectionStringOverride;
    }

    public string ConnectionString => _connectionStringOverride
        ?? _fixture?.ConnectionString
        ?? throw new InvalidOperationException("InitializeAsync has not run yet.");

    public async ValueTask InitializeAsync()
    {
        if (_connectionStringOverride is null)
        {
            _fixture = new PostgresFixture();
            await _fixture.InitializeAsync();
        }
        // Phase 8 deliberately does NOT pre-create the schema here — the migration
        // runs at first webapp boot via StartupCompletionService (D-15 swap landed
        // in Wave C 08-07). The model-shortcut create-from-model API would produce
        // a schema DIFFERENT from migrations (no FK constraint names, no uq_ prefix,
        // no xmin column).
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_fixture is not null)
        {
            await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
            {
                NpgsqlConnection.ClearPool(conn);
            }
            await _fixture.DisposeAsync();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = ConnectionString,
            });
        });

        base.ConfigureWebHost(builder);

        // No service-side rewiring: Wave C populates AppDbContext with real DbSets,
        // so AddBaseApi<AppDbContext> registers the production Repository<TEntity>
        // chain automatically. The 5 Phase 8 entity validators + mappers are
        // discovered via AddBaseApiValidation/AddBaseApiMapping's scan of
        // typeof(AppDbContext).Assembly (Phase 7 D-13).
    }
}
