using BaseApi.Core.DependencyInjection;
using BaseApi.Tests.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Tests.Validation;

/// <summary>
/// Phase 6 WAF subclass for SC#3 verification (RESEARCH Pitfall 5).
///
/// <para>
/// <c>Program.cs</c>'s <c>AddBaseApiValidation(typeof(Program).Assembly)</c> (Plan 06-01 D-18 wiring)
/// only scans <c>BaseApi.Service.dll</c>. <see cref="TestDtoValidator"/> lives in
/// <c>BaseApi.Tests.dll</c>, so <c>IValidator&lt;TestUpdateDto&gt;</c> resolves to null and
/// <see cref="TestValidationService"/>'s ctor injection fails with
/// <c>InvalidOperationException: Unable to resolve service for type 'FluentValidation.IValidator&lt;TestUpdateDto&gt;'</c>.
/// Resolution: re-scan the Tests assembly via <c>params Assembly[]</c> overload (Phase 6 D-16).
/// </para>
///
/// <para>
/// Plan 06-01 unsealed the base <see cref="WebAppFactory"/> (matching Phase 5's
/// <c>OtelCollectorFixture</c> unsealing precedent) so this subclass can override
/// <c>ConfigureWebHost</c>.
/// </para>
/// </summary>
public sealed class ValidationWebAppFactory : WebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            // Re-scan the Tests assembly so TestDtoValidator is discovered alongside
            // the Program-assembly validators (Pitfall 5).
            services.AddBaseApiValidation(typeof(ValidationWebAppFactory).Assembly);

            // TestValidationService consumes IValidator<TestUpdateDto> via ctor injection.
            services.AddScoped<TestValidationService>();
        });
    }
}
