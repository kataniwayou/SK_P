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
    // volatile backing field: the handle is written ONCE by RecoveryEndpointBinder.ExecuteAsync (one
    // hosted-service thread) and read on every L2-health edge by BitHealthLoop.ExecuteAsync (a different
    // hosted-service thread). Without a memory barrier the .NET memory model (ECMA CLI) does not guarantee
    // cross-thread visibility of the store, so on a relaxed-memory architecture the BIT loop could observe
    // null long after the binder set it — lengthening the startup window past T-52-11. volatile establishes
    // the acquire/release fence on read/write so the set is promptly visible to the reader thread.
    private volatile HostReceiveEndpointHandle? _handle;

    /// <summary>The connected keeper-recovery endpoint handle. Null until <see cref="RecoveryEndpointBinder"/>
    /// completes the runtime connect; thereafter <c>Handle.ReceiveEndpoint.Stop/Start</c> drives pause/resume.</summary>
    public HostReceiveEndpointHandle? Handle
    {
        get => _handle;
        set => _handle = value;
    }
}
