using MassTransit;

namespace Keeper.Recovery;

/// <summary>
/// D-04 (OQ-1): a mutable DI singleton holding the runtime-connected <c>keeper-recovery</c>
/// <see cref="HostReceiveEndpointHandle"/> so the BIT loop (Plan 03) can <c>Stop</c>/<c>Start</c> the
/// endpoint on L2-health edges. In MassTransit 8.5.5 a statically-configured (auto-config)
/// endpoint cannot be runtime-paused — <c>HostReceiveEndpointHandle.StopAsync</c> REMOVES it and it
/// cannot be cleanly restarted. The maintainer-blessed pause/resume pattern is
/// <c>IReceiveEndpointConnector.ConnectReceiveEndpoint</c> + <c>handle.ReceiveEndpoint.Stop/Start</c>,
/// which is mutually exclusive with static config (Pitfall 1). The processor uses exactly this pattern
/// (<c>ProcessorStartupOrchestrator</c>).
/// <para>
/// The handle is set ONCE by <see cref="RecoveryEndpointBinder"/> after <c>await handle.Ready</c>. It is
/// nullable because there is a brief window between bus start and the binder completing the connect; Plan 03
/// must null-check before driving Stop/Start.
/// </para>
/// </summary>
public sealed class RecoveryEndpointHandle
{
    /// <summary>The connected keeper-recovery endpoint handle. Null until <see cref="RecoveryEndpointBinder"/>
    /// completes the runtime connect; thereafter <c>Handle.ReceiveEndpoint.Stop/Start</c> drives pause/resume.</summary>
    public HostReceiveEndpointHandle? Handle { get; set; }
}
