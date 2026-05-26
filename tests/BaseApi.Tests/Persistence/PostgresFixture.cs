using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BaseApi.Tests.Persistence;

/// <summary>
/// Per-test-class fixture: creates a throwaway logical database inside the running
/// Phase 2 Postgres container (localhost:5433), runs EnsureCreatedAsync against it,
/// and DROPs it on dispose. xUnit v3 parallelizes test CLASSES by default, so each
/// class gets an isolated DB — no name collisions.
///
/// <para>
/// <b>Cleanup discipline (D-15 / Pitfall 26):</b> DisposeAsync clears all Npgsql
/// connection pools first (releases any lingering connections held by
/// DbContext disposal), then DROPs the database with the PG 13+ <c>WITH (FORCE)</c>
/// clause (terminates remaining sessions on the target DB). If DisposeAsync still
/// fails under heavy parallelism, the leaked DB surfaces in the Plan 03-02 SUMMARY
/// `psql \l` snapshot diff (manual cleanup acceptable per RESEARCH.md A4).
/// </para>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public string DatabaseName { get; } = $"stepsdb_test_{Guid.NewGuid():N}";
    public string ConnectionString { get; private set; } = default!;

    private const string AdminConnectionString =
        "Host=localhost;Port=5433;Database=postgres;Username=postgres;Password=postgres";

    public async ValueTask InitializeAsync()
    {
        await using var adminConn = new NpgsqlConnection(AdminConnectionString);
        await adminConn.OpenAsync();
        await using var createCmd = adminConn.CreateCommand();
        createCmd.CommandText = $"CREATE DATABASE \"{DatabaseName}\"";
        await createCmd.ExecuteNonQueryAsync();

        ConnectionString =
            $"Host=localhost;Port=5433;Database={DatabaseName};Username=postgres;Password=postgres";
    }

    public async ValueTask DisposeAsync()
    {
        // Clear connection pools BEFORE the DROP — otherwise the DROP fails with
        // "database is being accessed by other users" even though tests have disposed
        // their DbContexts (Npgsql keeps pooled connections around for reuse).
        NpgsqlConnection.ClearAllPools();

        await using var adminConn = new NpgsqlConnection(AdminConnectionString);
        await adminConn.OpenAsync();
        await using var dropCmd = adminConn.CreateCommand();
        // WITH (FORCE) is PG 13+; Phase 2 uses postgres:17-alpine (D-12).
        dropCmd.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)";
        await dropCmd.ExecuteNonQueryAsync();
    }
}
