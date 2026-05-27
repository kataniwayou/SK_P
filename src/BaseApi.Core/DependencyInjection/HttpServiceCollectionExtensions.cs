using Asp.Versioning;
using BaseApi.Core.Swagger;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Phase 7 HTTP wiring: AddControllers + Asp.Versioning.Mvc (controllers-correct package per
/// RESEARCH A-01) with URL-segment versioning + ApiExplorer + Swashbuckle. Pitfall 2:
/// AddApiVersioning().AddMvc().AddApiExplorer() MUST run BEFORE AddSwaggerGen() so the
/// <see cref="ConfigureSwaggerOptions"/> can resolve <c>IApiVersionDescriptionProvider</c>.
/// </summary>
internal static class HttpServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiHttp(
        this IServiceCollection services, IConfiguration cfg)
    {
        services.AddControllers();

        services.AddApiVersioning(opts =>
        {
            opts.DefaultApiVersion = new ApiVersion(1, 0);
            opts.AssumeDefaultVersionWhenUnspecified = true;
            opts.ReportApiVersions = true;
            // URL-segment reader is DEFAULT when [Route] template contains {version:apiVersion}.
        })
        .AddMvc()                                  // CRITICAL: requires Asp.Versioning.Mvc package (RESEARCH A-01)
        .AddApiExplorer(opts =>
        {
            opts.GroupNameFormat = "'v'VVV";        // "v1"
            opts.SubstituteApiVersionInUrl = true;  // {version:apiVersion} -> "1"
        });

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen();
        services.AddTransient<IConfigureOptions<SwaggerGenOptions>, ConfigureSwaggerOptions>();

        return services;
    }
}
