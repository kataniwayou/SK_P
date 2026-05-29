using System.Text.RegularExpressions;
using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>
/// Phase 12 INFRA-REDIS-04..05 — appsettings file-content regex assertions for the
/// Docker-internal vs host-side connection-string split (D-04), the Redis defaults
/// section (D-15), and the PITFALLS P2 PR-review-proof guard (abortConnect=false
/// in BOTH appsettings files). Defense-in-depth negatives for allowAdmin=true and
/// ssl=true adjacent to Redis (PITFALLS P32 / P3).
/// </summary>
[Trait("Phase12Wave", "C")]
public sealed class AppsettingsFacts
{
    private static string AppsettingsJsonContent()
    {
        var path = Path.Combine(FindRepoRoot(),
            "src", "BaseApi.Service", "appsettings.json");
        Assert.True(File.Exists(path), $"appsettings.json not found at {path}");
        return File.ReadAllText(path);
    }

    private static string AppsettingsDevelopmentJsonContent()
    {
        var path = Path.Combine(FindRepoRoot(),
            "src", "BaseApi.Service", "appsettings.Development.json");
        Assert.True(File.Exists(path), $"appsettings.Development.json not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Appsettings_Has_DockerInternal_Redis_ConnStr()
    {
        var content = AppsettingsJsonContent();
        Assert.Matches(
            new Regex(@"""Redis""\s*:\s*""redis:6379,abortConnect=false,connectTimeout=5000"""),
            content);
    }

    [Fact]
    public void AppsettingsDevelopment_Has_HostSide_Redis_ConnStr()
    {
        var content = AppsettingsDevelopmentJsonContent();
        Assert.Matches(
            new Regex(@"""Redis""\s*:\s*""localhost:6380,abortConnect=false,connectTimeout=5000"""),
            content);
    }

    [Fact]
    public void Appsettings_Has_Redis_Section_KeyPrefix_skp()
    {
        var content = AppsettingsJsonContent();
        Assert.Matches(new Regex(@"""KeyPrefix""\s*:\s*""skp:"""), content);
    }

    [Fact]
    public void Appsettings_Has_Redis_Serialization_JsonOptions_default()
    {
        var content = AppsettingsJsonContent();
        Assert.Matches(new Regex(@"""JsonOptions""\s*:\s*""default"""), content);
    }

    [Fact]
    public void Both_AppsettingsFiles_Contain_abortConnect_false()
    {
        // PITFALLS P2 PR-review-proof guard — Pitfall 2 line 733: the fail-loud
        // RequireConnectionString does NOT catch a missing abortConnect=false; only
        // an in-string assertion does. Verify BOTH files independently.
        Assert.Contains("abortConnect=false", AppsettingsJsonContent());
        Assert.Contains("abortConnect=false", AppsettingsDevelopmentJsonContent());
    }

    [Fact]
    public void Neither_AppsettingsFile_Contains_allowAdmin_true()
    {
        // PITFALLS P32 / T-12-03-03 defense-in-depth — allowAdmin=true would enable
        // FLUSHDB / CONFIG operations from app code.
        Assert.DoesNotContain("allowAdmin=true", AppsettingsJsonContent());
        Assert.DoesNotContain("allowAdmin=true", AppsettingsDevelopmentJsonContent());
    }

    [Fact]
    public void Neither_AppsettingsFile_Contains_ssl_true_AdjacentToRedis()
    {
        // PITFALLS P3 — TLS is deferred for local plaintext compose Redis; defense-
        // in-depth verifies no premature ssl=true addition.
        var prod = AppsettingsJsonContent();
        var dev = AppsettingsDevelopmentJsonContent();
        // Loose match — "Redis" key followed within ~200 chars by ssl=true.
        Assert.DoesNotMatch(new Regex(@"""Redis""[\s\S]{0,200}ssl=true"), prod);
        Assert.DoesNotMatch(new Regex(@"""Redis""[\s\S]{0,200}ssl=true"), dev);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SK_P.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate SK_P.sln walking up from " + AppContext.BaseDirectory);
    }
}
