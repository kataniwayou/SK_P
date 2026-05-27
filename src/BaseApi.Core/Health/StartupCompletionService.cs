using Microsoft.Extensions.Hosting;

namespace BaseApi.Core.Health;

/// <summary>
/// Phase 5 default-ready hook. Flips <see cref="IStartupGate.MarkReady"/> on host start
/// so <c>/health/startup</c> reports Healthy in v1 (no migrations yet).
///
/// <para>
/// <b>Phase 8 contract (D-13):</b> this type is REMOVED. A new
/// <c>MigrationRunner : IHostedService</c> registered FIRST in
/// <c>Program.cs</c>'s service collection runs <c>db.Database.MigrateAsync()</c>
/// THEN calls <c>_gate.MarkReady()</c>. Clean one-file deletion +
/// one new <c>AddHostedService&lt;MigrationRunner&gt;()</c> registration.
/// </para>
///
/// <para>
/// <b>Access modifier (deviation from CONTEXT D-01 wording):</b> <c>public sealed</c>
/// — same rationale as <see cref="StartupGate"/>:
/// <c>services.AddHostedService&lt;StartupCompletionService&gt;()</c> in
/// <c>BaseApi.Service.Program.cs</c> resolves the concrete type across the assembly boundary.
/// </para>
/// </summary>
public sealed class StartupCompletionService(IStartupGate gate) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        gate.MarkReady();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
