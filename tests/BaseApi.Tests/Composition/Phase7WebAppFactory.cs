using BaseApi.Core.DependencyInjection;
using BaseApi.Core.Services;
using BaseApi.Tests.Middleware;
using BaseApi.Tests.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BaseApi.Tests.Composition;

/// <summary>
/// WebAppFactory subclass for Phase 7 facts. Adds:
/// (a) <c>AddApplicationPart(typeof(Phase7WebAppFactory).Assembly)</c> so TestsController is discovered;
/// (b) <c>AddBaseApiValidation</c> + <c>AddBaseApiMapping</c> scanning the Tests assembly so
///     TestDtoValidator (TestUpdateDto), TestCreateDtoValidator (Blocker 2 fix), and TestEntityMapper
///     are visible to AddBaseApi's production scan (Phase 6 D-16 multi-assembly pattern — RESEARCH Pitfall 8);
/// (c) <c>AddScoped&lt;RecordingTestService&gt;()</c> AND alias
///     <c>AddScoped&lt;BaseService&lt;TestEntity,TestCreateDto,TestUpdateDto,TestReadDto&gt;&gt;(sp =&gt; sp.GetRequiredService&lt;RecordingTestService&gt;())</c>
///     — the alias is LOAD-BEARING because TestsController.ctor injects the abstract BaseService
///     (Warning 7 option b). Without the alias, DI cannot resolve the controller's dependency.
/// </summary>
public class Phase7WebAppFactory : WebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(Phase7WebAppFactory).Assembly);
            services.AddBaseApiValidation(typeof(Phase7WebAppFactory).Assembly);
            services.AddBaseApiMapping(typeof(Phase7WebAppFactory).Assembly);

            // Pitfall 10 + Warning 7 option b: register concrete recording service + LOAD-BEARING
            // alias (TestsController injects the abstract base).
            services.AddScoped<RecordingTestService>();
            services.AddScoped<BaseService<TestEntity, TestCreateDto, TestUpdateDto, TestReadDto>>(
                sp => sp.GetRequiredService<RecordingTestService>());
        });
    }
}
