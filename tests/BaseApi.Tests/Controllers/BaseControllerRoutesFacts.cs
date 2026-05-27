using BaseApi.Tests.Composition;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Controllers;

/// <summary>SC#1 — 5 CRUD verbs exposed at /api/v{version:apiVersion}/[controller] for TestsController.</summary>
public sealed class BaseControllerRoutesFacts
{
    [Fact]
    public async Task TestsController_Exposes_FiveCrudRoutes_UnderApiV1()
    {
        await using var factory = new Phase7WebAppFactory();
        await factory.InitializeAsync();
        using var scope   = factory.Services.CreateScope();
        var provider      = scope.ServiceProvider.GetRequiredService<IActionDescriptorCollectionProvider>();

        var descriptors = provider.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(a => a.ControllerTypeInfo.AsType() == typeof(TestsController))
            .ToList();

        Assert.Equal(5, descriptors.Count);

        var routes = descriptors.Select(d => new
        {
            Verb     = d.ActionConstraints?
                .OfType<HttpMethodActionConstraint>()
                .SelectMany(c => c.HttpMethods)
                .FirstOrDefault() ?? "",
            Template = d.AttributeRouteInfo?.Template ?? "",
        }).ToList();

        // Deviation [Rule 1 - Bug from plan body]: AttributeRouteInfo.Template resolves the
        // `[controller]` token to the literal controller name ("Tests") at runtime; the original
        // plan body assertion expected the un-substituted template literal which never appears
        // in the descriptor collection.
        Assert.Contains(routes, r => r.Verb == "GET"    && r.Template == "api/v{version:apiVersion}/Tests");
        Assert.Contains(routes, r => r.Verb == "GET"    && r.Template == "api/v{version:apiVersion}/Tests/{id:guid}");
        Assert.Contains(routes, r => r.Verb == "POST"   && r.Template == "api/v{version:apiVersion}/Tests");
        Assert.Contains(routes, r => r.Verb == "PUT"    && r.Template == "api/v{version:apiVersion}/Tests/{id:guid}");
        Assert.Contains(routes, r => r.Verb == "DELETE" && r.Template == "api/v{version:apiVersion}/Tests/{id:guid}");
    }
}
