namespace Orchestrator.Messaging;

/// <summary>
/// The configured L2 key prefix (<c>Redis:KeyPrefix</c>, "skp:") surfaced to the consumers as a
/// strongly-typed Singleton, so a consumer takes a typed dependency rather than raw
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>. Registered in
/// <c>Program.cs</c>; the harness test registers a known instance.
/// </summary>
public sealed record OrchestratorRedisOptions(string KeyPrefix);
