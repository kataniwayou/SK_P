using Microsoft.Extensions.Hosting;

namespace BaseConsole.Core.Health;

/// <summary>
/// Phase-5 startup-completion hosted service for the console host.
///
/// <para>
/// <c>StartAsync</c> flips the <see cref="IStartupGate"/> to ready as soon as the host has
/// finished initialization — there is no database or migration step on the console side, so
/// (unlike the Phase-8 API variant) this service injects only the gate: no EF context, no
/// scope factory, no database migration call. Doing so would drag EF Core into this library
/// and violate D-08.
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
