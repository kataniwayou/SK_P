namespace BaseApi.Tests.Composition;

/// <summary>
/// Phase8WebAppFactory variant that injects a deliberately-bad connection string
/// (host=localhost port=5434 — closed) so StartupCompletionService.MigrateAsync
/// throws at boot. Plan 08-08 MigrationFailureFacts proves the try/catch contract:
/// process responsive, readiness 503, no crash (PERSIST-10 + D-16).
/// <para>
/// Phase8WebAppFactory's protected ctor (Plan 08-01) sets <c>_connectionStringOverride</c>
/// and its <c>InitializeAsync</c> skips the real PostgresFixture allocation — this
/// means MigrationFailureWebAppFactory does NOT create a real DB. Its
/// <c>CreateClient()</c> boots a webapp whose StartupCompletionService will throw on
/// MigrateAsync against a closed port; the Phase 5 try/catch contract (Plan 08-07)
/// logs Critical, does NOT rethrow, does NOT MarkReady — process stays alive,
/// /health/startup and /health/ready return 503, /health/live returns 200.
/// </para>
/// </summary>
public sealed class MigrationFailureWebAppFactory : Phase8WebAppFactory
{
    public MigrationFailureWebAppFactory()
        : base("Host=localhost;Port=5434;Database=stepsdb;Username=postgres;Password=postgres")
    {
    }
}
