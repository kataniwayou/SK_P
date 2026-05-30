using System.Reflection;
using BaseConsole.Core.Configuration;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// CONSOLE-05 / D-08 regression guard — pure reflection, no host boot.
///
/// <para>
/// Walks the <c>BaseConsole.Core</c> assembly's <see cref="Assembly.GetReferencedAssemblies"/>
/// and asserts that the D-08 firewall has not been breached: none of the referenced assembly names
/// begins with <c>BaseApi.Core</c>, <c>Microsoft.EntityFrameworkCore</c>, or <c>Npgsql</c>.
/// </para>
///
/// <para>
/// The anchor type <see cref="RequiredConfig"/> is a stable, intentionally <c>public</c> type
/// in <c>BaseConsole.Core.Configuration</c> (made public specifically so composition-root
/// extensions in later Phase-18 plans can reach <see cref="RequiredConfig.RequireConnectionString"/>
/// without an <c>InternalsVisibleTo</c> annotation).
/// </para>
///
/// <para>
/// If a future phase introduces a forbidden reference, <c>dotnet test</c> catches it immediately
/// without a human grep pass (CONSOLE-05 automated regression).
/// </para>
/// </summary>
public sealed class ConsoleDependencyFirewallTests
{
    private static readonly Assembly BaseConsoleAssembly =
        typeof(RequiredConfig).Assembly;

    private static readonly string[] ForbiddenPrefixes =
    [
        "BaseApi.Core",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
    ];

    [Fact]
    public void BaseConsole_Core_has_no_BaseApiCore_reference()
    {
        var violating = GetViolatingReferences("BaseApi.Core");
        Assert.Empty(violating);
    }

    [Fact]
    public void BaseConsole_Core_has_no_EntityFrameworkCore_reference()
    {
        var violating = GetViolatingReferences("Microsoft.EntityFrameworkCore");
        Assert.Empty(violating);
    }

    [Fact]
    public void BaseConsole_Core_has_no_Npgsql_reference()
    {
        var violating = GetViolatingReferences("Npgsql");
        Assert.Empty(violating);
    }

    [Fact]
    public void BaseConsole_Core_has_no_forbidden_references()
    {
        // Consolidated assertion: none of the three forbidden families is referenced.
        var referenced = BaseConsoleAssembly
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
        BaseConsoleAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
