using Asp.Versioning.ApiExplorer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;       // SwaggerGenOptionsExtensions (SwaggerDoc / OperationFilter / DocumentFilter)
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BaseApi.Core.Swagger;

/// <summary>
/// Generates one Swagger document per discovered <see cref="ApiVersionDescription"/>.
/// Today only <c>v1</c> exists; adding <c>v2</c> in a later milestone is a single
/// <c>[ApiVersion("2.0")]</c> attribute on a sibling controller — this configurator
/// auto-emits the matching doc.
/// </summary>
internal sealed class ConfigureSwaggerOptions : IConfigureOptions<SwaggerGenOptions>
{
    private readonly IApiVersionDescriptionProvider _provider;
    private readonly IConfiguration _cfg;

    public ConfigureSwaggerOptions(IApiVersionDescriptionProvider provider, IConfiguration cfg)
    {
        _provider = provider;
        _cfg = cfg;
    }

    public void Configure(SwaggerGenOptions options)
    {
        foreach (var description in _provider.ApiVersionDescriptions)
        {
            options.SwaggerDoc(description.GroupName, new OpenApiInfo
            {
                Title       = _cfg["Service:Name"] ?? "sk-api",
                Version     = description.ApiVersion.ToString(),
                Description = "Steps API — workflow-engine CRUD foundation."
                            + (description.IsDeprecated ? " DEPRECATED." : ""),
            });
        }

        options.OperationFilter<CorrelationIdHeaderOperationFilter>();
        options.DocumentFilter<HideHealthEndpointsDocumentFilter>();
    }
}
