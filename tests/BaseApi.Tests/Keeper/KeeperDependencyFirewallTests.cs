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
///
/// <para>
/// NOTE — DIRECT references only: <see cref="Assembly.GetReferencedAssemblies"/> returns just the
/// references recorded in <c>Keeper.dll</c>'s own manifest; it does NOT walk the transitive closure.
/// A forbidden assembly pulled in indirectly via <c>BaseConsole.Core</c> or <c>Messaging.Contracts</c>
/// would NOT trip this guard. A full transitive scan would require a build-task / recursive load-and-walk
/// (deferred — and would diverge from the analogous <c>ConsoleDependencyFirewallTests</c>, which is also
/// direct-reference-only by design). This test enforces the DIRECT reference layer; "closure" here means
/// Keeper's own manifest, not the recursive dependency graph.
/// </para>
/// </summary>
public sealed class KeeperDependencyFirewallTests
{
    // Anchor on a Keeper type so the firewall reflects the actual Keeper.dll reference closure.
    private static readonly Assembly KeeperAssembly =
        typeof(global::Keeper.Consumers.FaultEntryStepDispatchConsumer).Assembly;

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
