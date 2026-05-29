using System.Text.RegularExpressions;
using Xunit;

namespace BaseApi.Tests.Features.Orchestration;

/// <summary>
/// Phase 15 forbidden-pattern guards — pure, fast source/reference assertions that the
/// solution never re-introduces the three banned Redis-projection patterns. These mitigate:
/// <list type="bullet">
///   <item><b>OBSERV-REDIS-01 / T-15-18</b> — no <c>OpenTelemetry.Instrumentation.StackExchangeRedis</c>
///     package anywhere (Phase 11 D-03 deliberately omits Redis tracing; re-adding it
///     re-introduces dropped/duplicate-span regressions).</item>
///   <item><b>L2-PROJECT-07 / T-15-19</b> — no <c>KEYS</c> / <c>IServer.Keys()</c> enumeration in
///     the Orchestration feature (Stop uses targeted GET-and-follow, not a keyspace scan; a
///     <c>KEYS</c> scan is an O(N) DoS on a shared Redis).</item>
///   <item><b>L2-PROJECT-06</b> — no source-generated object mapper (Mapperly) for JSON assembly
///     in the Projection folder (the L2 records serialize via plain System.Text.Json + camelCase
///     attributes; a mapper there would be the wrong tool and drift the wire shape).</item>
/// </list>
///
/// <para>
/// The guards read the solution's own files. To avoid false positives from documentation that
/// NAMES a forbidden pattern only to forbid it (e.g. <c>IRedisL2Cleanup</c>'s XML doc says it uses
/// "NO <c>KEYS</c> / <c>IServer.Keys()</c>"), the source scans strip line/block comments before
/// matching — the same comment-stripping discipline used by the Plan 03-01 verify scripts.
/// Repo root is resolved once by walking up from <see cref="AppContext.BaseDirectory"/> to
/// <c>SK_P.sln</c> (precedent: <c>PackageAuditTests</c> / <c>ComposeYamlFacts</c>).
/// </para>
/// </summary>
[Trait("Phase", "15")]
public sealed class RedisDisciplineGuardFacts
{
    /// <summary>OBSERV-REDIS-01 / T-15-18 — the OTel Redis instrumentation package is forbidden
    /// (any version). Asserts neither <c>Directory.Packages.props</c> nor any <c>.csproj</c>
    /// references it, and no loaded assembly carries its name.</summary>
    [Fact]
    public void No_OtelRedis_Package_Referenced()
    {
        const string forbidden = "OpenTelemetry.Instrumentation.StackExchangeRedis";
        var repoRoot = FindRepoRoot();

        // Directory.Packages.props (CPM — the only place a version would be pinned).
        var dpp = Path.Combine(repoRoot, "Directory.Packages.props");
        Assert.True(File.Exists(dpp), $"Directory.Packages.props not found at {dpp}");
        Assert.DoesNotContain(forbidden, File.ReadAllText(dpp), StringComparison.OrdinalIgnoreCase);

        // Every csproj (a PackageReference could in principle live there too).
        foreach (var csproj in Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(forbidden, File.ReadAllText(csproj), StringComparison.OrdinalIgnoreCase);
        }

        // Defense-in-depth — no loaded assembly with that simple name (would catch a transitive
        // resolution that somehow slipped past the file scans).
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty);
        Assert.DoesNotContain(loaded, n => n.Contains("StackExchangeRedis", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>L2-PROJECT-07 / T-15-19 — no Redis keyspace enumeration in the Orchestration
    /// feature. Matches the <c>IServer.Keys</c> / <c>server.Keys(</c> / <c>.KeysAsync(</c> / bare
    /// <c>KEYS</c>-command call shapes in CODE (comments stripped first), while tolerating the
    /// legitimate <c>Dictionary.Keys</c> PROPERTY access used by the in-memory validators.</summary>
    [Fact]
    public void No_Keys_Enumeration_In_Projection()
    {
        var repoRoot = FindRepoRoot();
        var featureDir = Path.Combine(
            repoRoot, "src", "BaseApi.Service", "Features", "Orchestration");
        Assert.True(Directory.Exists(featureDir), $"Orchestration feature dir not found at {featureDir}");

        // Redis enumeration call shapes (NOT the Dictionary<,>.Keys property):
        //   IServer.Keys(...) / server.Keys(...) / db.KeysAsync(...) / the raw KEYS command literal.
        var enumeration = new Regex(
            @"IServer\b|\.Keys\s*\(|\.KeysAsync\s*\(|\bKEYS\b",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var cs in Directory.GetFiles(featureDir, "*.cs", SearchOption.AllDirectories))
        {
            var code = StripComments(File.ReadAllText(cs));
            if (enumeration.IsMatch(code))
            {
                offenders.Add(cs);
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>L2-PROJECT-06 — no source-generated object mapper (Mapperly) for JSON assembly in
    /// the Projection folder. The L2 records serialize via plain System.Text.Json; Mapperly stays
    /// confined to the Loading folder's L1 enrichment (legitimate, out of this guard's scope).</summary>
    [Fact]
    public void No_Mapperly_For_Json_In_Projection()
    {
        var repoRoot = FindRepoRoot();
        var projectionDir = Path.Combine(
            repoRoot, "src", "BaseApi.Service", "Features", "Orchestration", "Projection");
        Assert.True(Directory.Exists(projectionDir), $"Projection dir not found at {projectionDir}");

        var mapperly = new Regex(
            @"Mapperly|\[Mapper\]|\.ToRead\s*\(",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var cs in Directory.GetFiles(projectionDir, "*.cs", SearchOption.AllDirectories))
        {
            var code = StripComments(File.ReadAllText(cs));
            if (mapperly.IsMatch(code))
            {
                offenders.Add(cs);
            }
        }

        Assert.Empty(offenders);
    }

    // ---- helpers ----

    /// <summary>
    /// Strips C# line comments (<c>//</c> and XML-doc <c>///</c>) and block comments
    /// (<c>/* ... */</c>) so a forbidden pattern that appears ONLY inside documentation (which
    /// names it to forbid it) does not trip a guard. Mirrors the Plan 03-01 verify-script
    /// comment-stripping discipline. Not a full lexer (does not exempt the patterns appearing
    /// inside string literals), but the Orchestration sources carry no such string literals, and
    /// any future one would be a deliberate signal worth surfacing.
    /// </summary>
    private static string StripComments(string source)
    {
        // Block comments first (non-greedy, across newlines), then single-line comments.
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//.*", string.Empty);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SK_P.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate SK_P.sln by walking up from AppContext.BaseDirectory");
    }
}
