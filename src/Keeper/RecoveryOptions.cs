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
    public int GateWaitSeconds { get; set; } = 300;  // D-03 — bounded gate-wait, under the 30-min consumer_timeout
}
