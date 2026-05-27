using Xunit;

namespace BaseApi.Tests.Validation;

/// <summary>
/// VALID-01 — solution-wide audit that <c>FluentValidation.AspNetCore</c> (deprecated, removed
/// in FluentValidation 12) is NOT referenced anywhere in the repo.
///
/// <para>
/// Scans every <c>.csproj</c> file under the repo root for the forbidden string.
/// The repo root is discovered by walking up from the test executable's directory
/// until <c>SK_P.sln</c> is found.
/// </para>
/// </summary>
public sealed class PackageAuditTests
{
    [Fact]
    public void Test_NoFluentValidationAspNetCore_ReferencedAnywhere()
    {
        var repoRoot = FindRepoRoot();
        var csprojFiles = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories);

        Assert.NotEmpty(csprojFiles);  // sanity — we should find at least 3 csproj

        var offenders = new List<string>();
        foreach (var csproj in csprojFiles)
        {
            var content = File.ReadAllText(csproj);
            if (content.Contains("FluentValidation.AspNetCore", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(csproj);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Test_NoFluentValidationAspNetCore_InDirectoryPackagesProps()
    {
        var repoRoot = FindRepoRoot();
        var dpp = Path.Combine(repoRoot, "Directory.Packages.props");
        Assert.True(File.Exists(dpp), $"Directory.Packages.props not found at {dpp}");

        var content = File.ReadAllText(dpp);
        Assert.DoesNotContain("FluentValidation.AspNetCore", content, StringComparison.OrdinalIgnoreCase);
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
