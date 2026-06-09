namespace Keeper;

/// <summary>D-03/D-06: recovery-consumer knobs, bound from the "Recovery" appsettings section
/// (mirrors <see cref="ProbeOptions"/> / <see cref="BackupOptions"/>). <see cref="PartitionCount"/> is
/// the MassTransit UsePartitioner slot count (D-06, default 8). <see cref="GateWaitSeconds"/> bounds the
/// once-at-entry IL2HealthGate await via a linked CTS (D-03, default 300s — well under RabbitMQ's
/// default 30-min consumer_timeout; on bound exhaustion a transient marker routes through the endpoint
/// UseMessageRetry).</summary>
public sealed class RecoveryOptions
{
    public int PartitionCount  { get; set; } = 8;    // D-06 default

    /// <summary>D-03: bounds the once-at-entry IL2HealthGate await via a linked CTS (default 300s).
    /// <para>OPERATIONAL COUPLING (WR-02): a parked recovery <c>Consume</c> holds its broker channel for up
    /// to this many seconds, so <see cref="GateWaitSeconds"/> MUST remain below the deployed RabbitMQ
    /// <c>consumer_timeout</c> (broker default 30 min). If an operator lowers <c>consumer_timeout</c> below
    /// this value, a parked recovery Consume is FORCE-CLOSED by the broker and the channel is dropped. The
    /// two values live in different config systems (this app vs. the broker) and CANNOT be validated
    /// together at build time — there is deliberately no runtime assertion against the broker; keep them in
    /// sync operationally.</para></summary>
    public int GateWaitSeconds { get; set; } = 300;  // D-03 — bounded gate-wait, MUST stay below broker consumer_timeout (WR-02)
}
