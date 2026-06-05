using System.Reflection;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// KEEP-01 reference-closure guard (D-07) — pure reflection, no host boot. Walks the Keeper assembly's
/// <see cref="Assembly.GetReferencedAssemblies"/> and asserts the firewall holds: none of the referenced
/// assembly names begins with <c>BaseApi.Core</c>, <c>Microsoft.EntityFrameworkCore</c>, <c>Npgsql</c>,
/// <c>Quartz</c>, or <c>Cronos</c> — Keeper does not touch the API base, EF/Postgres, OR the scheduler
/// (it has no cron math). If a future phase introduces a forbidden reference, <c>dotnet test</c> catches
/// it immediately without a human grep pass.
/// </summary>
public sealed class KeeperDependencyFirewallTests
{
    // Anchor on a Keeper type so the firewall reflects the actual Keeper.dll reference closure.
    private static readonly Assembly KeeperAssembly =
        typeof(global::Keeper.Consumers.PlaceholderConsumer).Assembly;

    private static readonly string[] ForbiddenPrefixes =
    [
        "BaseApi.Core",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Quartz",        // D-07 — Keeper does not schedule
        "Cronos",        // D-07 — Keeper has no cron math
    ];

    [Fact]
    public void Keeper_Has_No_Forbidden_References()
    {
        var violating = KeeperAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => ForbiddenPrefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(violating);
    }
}
