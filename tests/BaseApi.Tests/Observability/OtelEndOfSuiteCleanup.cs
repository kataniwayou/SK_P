using System.Diagnostics;
using Xunit;

[assembly: AssemblyFixture(typeof(BaseApi.Tests.Observability.OtelEndOfSuiteCleanup))]

namespace BaseApi.Tests.Observability;

/// <summary>
/// xUnit v3 assembly-level fixture (registered via the
/// <c>[assembly: AssemblyFixture(...)]</c> attribute above) — runs once at the START
/// of the entire test assembly via the parameterless ctor (no-op), then once at the END
/// via <see cref="DisposeAsync"/>.
///
/// <para>
/// <b>Purpose:</b> close the Plan 05-02 telemetry.jsonl AMBER. The otel-collector
/// container's file exporter holds an exclusive write handle on
/// <c>tests/.otel-out/telemetry.jsonl</c> for the container's lifetime; the per-test
/// <see cref="OtelCollectorFixture"/>.<c>DisposeAsync</c> cannot delete the file from
/// inside the test process without orphaning the inode the exporter is writing to.
/// Plan 05-01 D-11 mandates the file does NOT survive past the test session.
/// </para>
///
/// <para>
/// <b>Mechanism:</b> in <see cref="DisposeAsync"/>, shell out to <c>docker compose</c>:
/// <list type="number">
///   <item><c>docker compose stop otel-collector</c> — releases the file handle.</item>
///   <item>Delete <c>tests/.otel-out/telemetry.jsonl</c> + any rotation siblings
///         (<c>telemetry-*.jsonl</c>) — but preserve <c>.gitkeep</c> so the dir survives
///         <c>git clean</c>.</item>
///   <item><c>docker compose start otel-collector</c> — restart for future runs (so
///         subsequent <c>dotnet test</c> invocations don't require manual intervention).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Best-effort discipline:</b> every step swallows failures and logs to stderr. The
/// fixture MUST NOT throw out of <see cref="DisposeAsync"/> — that would mask a successful
/// test run with a cleanup-only failure. If <c>docker compose</c> is not installed (CI
/// environment without Docker), the fixture logs and exits cleanly; the file just
/// persists (acceptable per Plan 05-02 deferred-cleanup posture in non-Docker contexts).
/// </para>
///
/// <para>
/// <b>Race safety:</b> assembly fixtures are constructed once before any tests run and
/// disposed once after ALL tests complete (xUnit v3 contract — see xunit.net docs on
/// shared context / assembly fixtures). No test code observes telemetry.jsonl during
/// <see cref="DisposeAsync"/> because the test phase is over by then.
/// </para>
/// </summary>
public sealed class OtelEndOfSuiteCleanup : IAsyncLifetime
{
    /// <summary>Absolute path to the telemetry file (same resolver as OtelCollectorFixture).</summary>
    private static readonly string TelemetryFile = OtelCollectorFixture.TelemetryFile;

    /// <summary>Absolute path to the solution root (parent of tests/.otel-out/).</summary>
    private static readonly string SolutionRoot = ResolveSolutionRoot();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // Step 1: stop otel-collector to release the exclusive write handle.
        var stopOk = await RunDockerComposeAsync(["stop", "otel-collector"], timeoutSec: 15);
        if (!stopOk)
        {
            await Console.Error.WriteLineAsync(
                "[OtelEndOfSuiteCleanup] WARN — docker compose stop otel-collector did not " +
                "complete cleanly; telemetry.jsonl may persist.");
            return;
        }

        // Step 2: delete telemetry.jsonl + any rotation siblings.
        try
        {
            var dir = Path.GetDirectoryName(TelemetryFile);
            if (dir is not null && Directory.Exists(dir))
            {
                // Delete the main file
                if (File.Exists(TelemetryFile)) File.Delete(TelemetryFile);
                // Delete rotation siblings (file exporter writes telemetry-N.jsonl on rotation)
                foreach (var rotated in Directory.EnumerateFiles(dir, "telemetry*.jsonl"))
                {
                    try { File.Delete(rotated); }
                    catch (IOException) { /* best-effort */ }
                }
                // NOTE: .gitkeep is NOT a telemetry*.jsonl, so it survives the glob above.
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"[OtelEndOfSuiteCleanup] WARN — file deletion failed: {ex.Message}");
        }

        // Step 3: restart otel-collector so subsequent test runs work without manual intervention.
        var startOk = await RunDockerComposeAsync(["start", "otel-collector"], timeoutSec: 15);
        if (!startOk)
        {
            await Console.Error.WriteLineAsync(
                "[OtelEndOfSuiteCleanup] WARN — docker compose start otel-collector did not " +
                "complete cleanly; next `dotnet test` may need a manual `docker compose up -d`.");
        }
    }

    /// <summary>
    /// Runs <c>docker compose &lt;args&gt;</c> with cwd = solution root. Returns true if
    /// the process exited 0 within the timeout. Swallows all exceptions (Docker may be
    /// absent or busy in CI).
    /// </summary>
    private static async Task<bool> RunDockerComposeAsync(string[] args, int timeoutSec)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                WorkingDirectory      = SolutionRoot,
                UseShellExecute       = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("compose");
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return false;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"[OtelEndOfSuiteCleanup] docker compose {string.Join(' ', args)} failed: {ex.Message}");
            return false;
        }
    }

    private static string ResolveSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("SK_P.sln").Any())
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException(
                "OtelEndOfSuiteCleanup: cannot locate solution root (SK_P.sln) from test base dir");
        return dir.FullName;
    }
}
