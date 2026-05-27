using BaseApi.Tests.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace BaseApi.Tests.Composition;

/// <summary>
/// WebAppFactory subclass overriding the host environment to Production for SC#4
/// `/swagger` -> 404 verification. WebApplicationFactory defaults to Development;
/// overriding via <c>builder.UseEnvironment(Environments.Production)</c> in
/// <see cref="CreateHost"/> is the canonical pattern (Microsoft Learn integration tests).
/// </summary>
internal sealed class ProductionWebAppFactory : WebAppFactory
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        return base.CreateHost(builder);
    }
}
