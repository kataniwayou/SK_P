namespace Keeper.Health;

/// <summary>KEEP-03 (D-09): the L2 BIT health gate. Writer = BitHealthLoop; reader = Phase-46 recovery consumer.</summary>
public interface IL2HealthGate
{
    void Open();
    void Close();
    Task WaitForOpenAsync(CancellationToken ct);
}
