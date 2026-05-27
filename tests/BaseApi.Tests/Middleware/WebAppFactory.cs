using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Tests.Middleware;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> wrapper that:
/// <list type="number">
///   <item>Registers the test assembly's controllers via
///         <c>AddApplicationPart(typeof(WebAppFactory).Assembly)</c> so
///         <c>[ApiController]</c> in <c>BaseApi.Tests.Endpoints.TestController</c>
///         is discoverable by the in-process TestServer. Source: Plan 04-02
///         RESEARCH.md Code Example "WebApplicationFactory Test Seam".</item>
///   <item>Optionally registers a <see cref="TestErrorDbContext"/> bound to the
///         per-class throwaway <c>PostgresFixture.ConnectionString</c> (used by
///         <c>SqlStateMappingTests</c> and <c>ConcurrencyTokenTests</c>; not
///         needed by <c>CorrelationIdTests</c> or <c>NotFoundAndUnhandledTests</c>
///         — those pass <c>null</c> to skip the DB wiring).</item>
/// </list>
///
/// <para>
/// <see cref="Program"/> resolves to the partial class marker in
/// <c>src/BaseApi.Service/Program.cs</c> line 72 — load-bearing dependency.
/// </para>
/// </summary>
public sealed class WebAppFactory : WebApplicationFactory<Program>
{
    private readonly string? _connectionString;

    public WebAppFactory() : this(null) { }

    public WebAppFactory(string? connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Register the test assembly's [ApiController] types via assembly part discovery.
            services.AddControllers()
                .AddApplicationPart(typeof(WebAppFactory).Assembly);

            if (_connectionString is not null)
            {
                services.AddDbContext<TestErrorDbContext>(opts =>
                    opts.UseNpgsql(_connectionString)
                        .UseSnakeCaseNamingConvention());
            }
        });
    }
}
