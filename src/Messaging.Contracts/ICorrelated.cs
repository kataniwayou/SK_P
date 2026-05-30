namespace Messaging.Contracts;

/// <summary>Universal correlation contract — body-carried correlation id (v3.4.0 model, D-01).</summary>
public interface ICorrelated
{
    Guid CorrelationId { get; }
}
