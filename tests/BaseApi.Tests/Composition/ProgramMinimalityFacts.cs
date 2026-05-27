using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>SC#3 — Program.cs is a thin file containing AddBaseApi + UseBaseApi + MapControllers
/// + AddBaseApiObservability (D-13 amendment) and no per-concern wiring.</summary>
public sealed class ProgramMinimalityFacts
{
    /// <summary>
    /// WR-04 fix: anchor on the solution root by walking up from <see cref="AppContext.BaseDirectory"/>
    /// looking for the marker file <c>SK_P.sln</c>. This replaces the brittle
    /// <c>".." x 5</c> traversal that assumed
    /// <c>tests/BaseApi.Tests/bin/{Configuration}/net8.0/</c> as the exact output layout
    /// (broken by multi-targeting, <c>AppendTargetFrameworkToOutputPath=false</c>,
    /// CI-overridden output paths, or self-contained publish).
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SK_P.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate SK_P.sln walking up from " + AppContext.BaseDirectory);
    }

    private static string ProgramCsPath()
        => Path.Combine(FindRepoRoot(), "src", "BaseApi.Service", "Program.cs");

    private static string ProgramCsContent()
    {
        var path = ProgramCsPath();
        Assert.True(File.Exists(path), $"Program.cs not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void ProgramCs_Contains_AddBaseApi_Call()
    {
        Assert.Contains("AddBaseApi<", ProgramCsContent());
    }

    [Fact]
    public void ProgramCs_Contains_UseBaseApi_Call()
    {
        Assert.Contains("UseBaseApi(", ProgramCsContent());
    }

    [Fact]
    public void ProgramCs_Contains_MapControllers_Call()
    {
        Assert.Contains("MapControllers", ProgramCsContent());
    }

    [Fact]
    public void ProgramCs_Contains_AddBaseApiObservability_Call()
    {
        // D-13 amendment (Plan 07-01 <context_deviation>): Observability is invoked
        // separately on IHostApplicationBuilder because the OTel MEL bridge needs
        // ILoggingBuilder. Asserting this positive presence guards against accidental
        // regression to the original (impossible) 7-call-inside-IServiceCollection form.
        Assert.Contains("AddBaseApiObservability", ProgramCsContent());
    }

    [Fact]
    public void ProgramCs_DoesNotContain_PerConcernWiring()
    {
        var content = ProgramCsContent();
        Assert.DoesNotContain("AddOpenTelemetry",  content);
        Assert.DoesNotContain("AddHealthChecks",   content);
        Assert.DoesNotContain("AddExceptionHandler<", content);
        Assert.DoesNotContain("AddDbContext<",     content);
        Assert.DoesNotContain("AddSwaggerGen",     content);
        Assert.DoesNotContain("AddApiVersioning",  content);
        Assert.DoesNotContain("MapHealthChecks",   content);
        Assert.DoesNotContain("UseExceptionHandler()", content);
    }

    [Fact]
    public void ProgramCs_BodyLines_LessThan_OrEqualTo_Ten()
    {
        var nonTrivialLines = File.ReadAllLines(ProgramCsPath())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.TrimStart().StartsWith("//"))
            .Where(line => !line.TrimStart().StartsWith("using "))
            .Where(line => !line.Trim().StartsWith("public partial class Program"))
            .ToList();

        Assert.True(nonTrivialLines.Count <= 10,
            $"Program.cs has {nonTrivialLines.Count} non-trivial body lines — expected <= 10 (SC#3).");
    }
}
