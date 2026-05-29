using BaseApi.Tests.Middleware;
using BaseApi.Tests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;
using StackExchange.Redis;
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
    private readonly bool _skipPostgresFixture;
    private RedisFixture? _redisFixture;
    private readonly string? _redisConnectionStringOverride;
    private readonly bool _skipRedisFixture;

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

    /// <summary>
    /// WR-04 review fix: constructor variant that SKIPS the real PostgresFixture
    /// testcontainer boot. Used by health-dead-postgres tests
    /// (<c>HealthDeadPostgresFixture</c> in
    /// <see cref="BaseApi.Tests.Observability.HealthEndpointsTests"/>) which explicitly
    /// want Postgres to be UNREACHABLE — spinning up a real testcontainer just to
    /// ignore it costs ~10s per fixture instance × 4 facts = ~40s of waste per run.
    /// </summary>
    /// <param name="skipPostgresFixture">When <c>true</c>, <see cref="InitializeAsync"/>
    /// does NOT construct a <see cref="PostgresFixture"/>. Callers MUST supply
    /// <paramref name="connectionStringOverride"/> so <see cref="ConnectionString"/>
    /// is resolvable.</param>
    /// <param name="connectionStringOverride">Connection string returned by
    /// <see cref="ConnectionString"/> — typically a dead-port string for the
    /// health-dead-postgres tests.</param>
    protected Phase8WebAppFactory(bool skipPostgresFixture, string connectionStringOverride)
    {
        // IN-10 review fix: enforce the XML doc invariant at construction time so a
        // caller violating "skipPostgresFixture=true requires non-empty
        // connectionStringOverride" fails fast at ctor instead of deferring to the
        // first ConnectionString property read (which surfaces as a confusing
        // InvalidOperationException downstream). The nullable-reference-type compiler
        // hint alone is advisory — null! suppression bypasses it silently.
        if (skipPostgresFixture && string.IsNullOrEmpty(connectionStringOverride))
        {
            throw new ArgumentException(
                "skipPostgresFixture=true requires a non-empty connectionStringOverride.",
                nameof(connectionStringOverride));
        }
        _skipPostgresFixture = skipPostgresFixture;
        _connectionStringOverride = connectionStringOverride;
    }

    /// <summary>
    /// Phase 12 D-07 — 4-arg ctor for HealthDeadRedisFixture (Plan 12-06). Lets a
    /// subclass independently skip Postgres / Redis fixture creation and supply a
    /// dead/static connection string. Mirrors the existing 2-arg Postgres-only overload.
    /// </summary>
    protected Phase8WebAppFactory(
        bool skipPostgresFixture, string connectionStringOverride,
        bool skipRedisFixture, string redisConnectionStringOverride)
    {
        if (skipPostgresFixture && string.IsNullOrEmpty(connectionStringOverride))
        {
            throw new ArgumentException(
                "skipPostgresFixture=true requires a non-empty connectionStringOverride.",
                nameof(connectionStringOverride));
        }
        if (skipRedisFixture && string.IsNullOrEmpty(redisConnectionStringOverride))
        {
            throw new ArgumentException(
                "skipRedisFixture=true requires a non-empty redisConnectionStringOverride.",
                nameof(redisConnectionStringOverride));
        }
        _skipPostgresFixture = skipPostgresFixture;
        _connectionStringOverride = connectionStringOverride;
        _skipRedisFixture = skipRedisFixture;
        _redisConnectionStringOverride = redisConnectionStringOverride;
    }

    public string ConnectionString => _connectionStringOverride
        ?? _fixture?.ConnectionString
        ?? throw new InvalidOperationException("InitializeAsync has not run yet.");

    public string RedisConnectionString => _redisConnectionStringOverride
        ?? _redisFixture?.ConnectionString
        ?? throw new InvalidOperationException("InitializeAsync has not run yet.");

    public string RedisKeyPrefix => _redisFixture?.KeyPrefix
        ?? "test:cls-deadredis:";   // benign placeholder for skipRedisFixture path

    /// <summary>
    /// Phase 16 access surface (RESEARCH Open Q4) — TEST-REDIS-06..09 facts need
    /// direct multiplexer access for 3-keyspace assertions. Throws if no live
    /// RedisFixture exists (skipRedisFixture path).
    /// </summary>
    public IConnectionMultiplexer RedisMultiplexer => _redisFixture?.Multiplexer
        ?? throw new InvalidOperationException(
            "No live RedisFixture — this Phase8WebAppFactory instance was constructed " +
            "with skipRedisFixture=true (e.g., HealthDeadRedisFixture).");

    public async ValueTask InitializeAsync()
    {
        // WR-04 review fix: skip testcontainer boot for fixtures that explicitly want
        // Postgres unreachable. The dead-port override on _connectionStringOverride is
        // returned by ConnectionString and surfaces via the standard
        // ConfigureAppConfiguration override below.
        // Phase 8 deliberately does NOT pre-create the schema here — the migration
        // runs at first webapp boot via StartupCompletionService (D-15 swap landed
        // in Wave C 08-07). The model-shortcut create-from-model API would produce
        // a schema DIFFERENT from migrations (no FK constraint names, no uq_ prefix,
        // no xmin column).
        if (!_skipPostgresFixture && _connectionStringOverride is null)
        {
            _fixture = new PostgresFixture();
            await _fixture.InitializeAsync();
        }

        if (!_skipRedisFixture)
        {
            _redisFixture = new RedisFixture();
            try
            {
                await _redisFixture.InitializeAsync();
            }
            catch
            {
                // RESEARCH Pitfall 4 — defensive Postgres rollback on Redis init failure
                // so the throwaway DB does not leak when the caller doesn't await using.
                if (_fixture is not null) await _fixture.DisposeAsync();
                throw;
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        // Order: Redis first (releases multiplexer + asserts SCAN-zero cleanup),
        // then Postgres (NpgsqlConnection.ClearPool + DROP DATABASE WITH FORCE).
        if (_redisFixture is not null) await _redisFixture.DisposeAsync();
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
                // D-08 — AddInMemoryCollection injection for Redis (Plan 05-02 Pattern C
                // applied verbatim WITHOUT the env-var workaround the Postgres side needed).
                // AddBaseApiRedis reads cfg.GetConnectionString("Redis") inside the
                // Singleton factory closure at first IConnectionMultiplexer resolution
                // (D-17 lazy), which is AFTER ConfigureWebHost runs. No value-capture
                // race (RESEARCH Assumption A4 HIGH confidence).
                ["ConnectionStrings:Redis"] = RedisConnectionString,
                ["Redis:KeyPrefix"] = RedisKeyPrefix,
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
