using System.Reflection;
using BaseApi.Core.DependencyInjection;
using Xunit;

// NOTE: namespace is deliberately BaseApi.Tests.MessagingResponders (NOT BaseApi.Tests.Messaging).
// A "BaseApi.Tests.Messaging" namespace would shadow the top-level "Messaging" namespace for every
// sibling test file (C# resolves unqualified "Messaging.Contracts..." to the nearest enclosing
// "BaseApi.Tests.Messaging.Contracts" first), breaking existing files that reference
// Messaging.Contracts.* unqualified. The files still live under the tests/.../Messaging/ folder.
namespace BaseApi.Tests.MessagingResponders;

/// <summary>
/// RPC-03 / CONTRACT-01 / T-25-02-04 dependency-firewall regression guard — pure reflection, no host boot.
///
/// <para>
/// Mirrors <c>ConsoleDependencyFirewallTests</c>: walks the <c>BaseApi.Core</c> assembly's
/// <see cref="Assembly.GetReferencedAssemblies"/> and asserts that the Phase-19 firewall is still
/// intact AFTER Plan 25-02 added the two optional <c>configureConsumers</c>/<c>configureEndpoints</c>
/// hooks to <c>AddBaseApiMessaging</c>: none of the referenced assembly names begins with
/// <c>BaseApi.Service</c> or <c>BaseConsole.Core</c>.
/// </para>
///
/// <para>
/// The hooks are typed in MassTransit interfaces only (<c>IBusRegistrationConfigurator</c> /
/// <c>IBusRegistrationContext</c> / <c>IRabbitMqBusFactoryConfigurator</c>) — Core never names a
/// concrete consumer type — so the projection from a publish-only join into a responder host adds
/// NO assembly reference to the WebApi (<c>BaseApi.Service</c>) or the console base
/// (<c>BaseConsole.Core</c>). If a future phase introduces a forbidden reference, <c>dotnet test</c>
/// catches it immediately without a human grep pass.
/// </para>
///
/// <para>
/// The anchor type <see cref="MessagingServiceCollectionExtensions"/> is a stable, public type in
/// <c>BaseApi.Core.DependencyInjection</c> — the very file the hooks were added to.
/// </para>
/// </summary>
public sealed class BaseApiCoreFirewallTests
{
    private static readonly Assembly BaseApiCoreAssembly =
        typeof(MessagingServiceCollectionExtensions).Assembly;

    private static readonly string[] ForbiddenPrefixes =
    [
        "BaseApi.Service",
        "BaseConsole.Core",
    ];

    [Fact]
    public void BaseApi_Core_has_no_BaseApiService_reference()
    {
        var violating = GetViolatingReferences("BaseApi.Service");
        Assert.Empty(violating);
    }

    [Fact]
    public void BaseApi_Core_has_no_BaseConsoleCore_reference()
    {
        var violating = GetViolatingReferences("BaseConsole.Core");
        Assert.Empty(violating);
    }

    [Fact]
    public void BaseApi_Core_has_no_forbidden_references()
    {
        // Consolidated assertion: neither forbidden family is referenced.
        var referenced = BaseApiCoreAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        var violating = referenced
            .Where(name => ForbiddenPrefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(violating);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static IReadOnlyList<string> GetViolatingReferences(string forbiddenPrefix) =>
        BaseApiCoreAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
