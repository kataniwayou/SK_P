using BaseConsole.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace BaseConsole.Core.DependencyInjection;

/// <summary>
/// Console-side Redis client wiring: a single <see cref="IConnectionMultiplexer"/> Singleton
/// (CONSOLE-03). Soft dependency — no startup probe, no health check.
///
/// <para>
/// <c>abortConnect=false</c> is supplied by the caller's appsettings connection string (NOT
/// hardcoded here), so boot never crashes on a dead Redis: the multiplexer materializes even if
/// Redis is unreachable, and subsequent operations throw <c>RedisConnectionException</c> at the
/// call site. Connection fires lazily at first resolution (no pre-warm hosted service).
/// </para>
///
/// <para>
/// Unlike the API base library variant, this extension does NOT bind a projection-options type
/// (that type is an EF-coupled API concern; binding it would violate D-08). The Singleton
/// lifetime is the StackExchange.Redis maintainer-blessed pattern — the multiplexer is
/// thread-safe and designed for long-lived reuse.
/// </para>
/// </summary>
public static class ConsoleRedisServiceCollectionExtensions
{
    public static IServiceCollection AddBaseConsoleRedis(
        this IServiceCollection services, IConfiguration cfg)
    {
        var connStr = cfg.RequireConnectionString("Redis");
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connStr));
        return services;
    }
}
