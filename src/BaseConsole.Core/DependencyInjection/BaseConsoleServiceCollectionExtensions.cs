using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BaseConsole.Core.DependencyInjection;

/// <summary>
/// The non-generic console composition root (CONSOLE-01, D-07). A single <c>AddBaseConsole(cfg)</c>
/// call chains the soft-dependency Redis singleton (Plan 01) and the health surface (the startup gate,
/// the self/startup checks, the Phase-5 completion service, and the embedded minimal-Kestrel listener).
///
/// <para>
/// <b>Deliberately NOT in this chain.</b>
/// <list type="bullet">
///   <item><b>Observability</b> — the console OTel extension is a separate call on
///   <c>IHostApplicationBuilder</c> (Plan 02), not on <see cref="IServiceCollection"/>.</item>
///   <item><b>Messaging</b> — the bus-skeleton extension is a separate call carrying the
///   consumer-registration lambda (D-06), so the base supplies infra while the concrete console supplies
///   its consumers.</item>
/// </list>
/// The three-call seam (this root + the OTel call + the bus call) is what the Plan 04 fixture exercises.
/// </para>
///
/// <para>
/// <b>D-07:</b> non-generic — there is no <c>TDbContext</c> type parameter. Consoles have no DbContext.
/// </para>
/// </summary>
public static class BaseConsoleServiceCollectionExtensions
{
    public static IServiceCollection AddBaseConsole(this IServiceCollection services, IConfiguration cfg)
        => services
            .AddBaseConsoleRedis(cfg)     // soft-dep singleton multiplexer (Plan 01)
            .AddBaseConsoleHealth(cfg);   // gate + self/startup checks + completion service + embedded listener
}
