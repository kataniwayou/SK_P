using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Processor.Sample;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Hermetic reflection facts for the build-time SourceHash embed (IDENT-01/02 / D-08). These READ
/// the value embedded onto the built <c>Processor.Sample.dll</c> exactly as the runtime reader does
/// (<see cref="AssemblyMetadataAttribute"/> off the assembly) — they do NOT recompute the hash, so
/// they prove the embed mechanism rather than re-implementing the algorithm.
/// </summary>
public sealed class SourceHashEmbedFacts
{
    [Fact]
    public void Embedded_SourceHash_Is_Lowercase_64_Hex()
    {
        // D-08: reflect the GENUINE embedded value off the built Processor.Sample assembly, the same
        // way AssemblyMetadataSourceHashProvider does at runtime (GetEntryAssembly equivalent).
        var hash = typeof(SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;

        Assert.Matches(new Regex("^[a-f0-9]{64}$"), hash);
    }

    [Fact]
    public void ImplFiles_Fold_Excludes_OutOfScope_And_Generated_Files()
    {
        // IDENT-01: the @(ImplFiles) fold must cover BaseProcessor.Core + Processor.Sample .cs ONLY —
        // BaseConsole.Core and Messaging.Contracts are siblings (excluded automatically), and
        // generated files (*.g.cs / GlobalUsings / AssemblyInfo) never participate. Dump the live
        // item set via the SourceHash.targets DumpImplFiles target and assert no excluded file leaks.
        var dump = Path.Combine(Path.GetTempPath(), $"implfiles-{Guid.NewGuid():N}.txt");
        try
        {
            var sampleDir = LocateProcessorSampleProjectDir();
            var psi = new ProcessStartInfo("dotnet",
                $"build \"{sampleDir}\" -t:DumpImplFiles \"/p:ImplFilesDump={dump}\" -nologo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            Assert.True(proc.ExitCode == 0, $"DumpImplFiles build failed:\n{stdout}\n{stderr}");
            Assert.True(File.Exists(dump), $"ImplFiles dump not produced:\n{stdout}\n{stderr}");

            var lines = File.ReadAllLines(dump);
            Assert.NotEmpty(lines);

            foreach (var line in lines)
            {
                var normalized = line.Replace('\\', '/');
                Assert.DoesNotContain("/BaseConsole.Core/", normalized);
                Assert.DoesNotContain("/Messaging.Contracts/", normalized);
                Assert.DoesNotMatch(new Regex(@"\.g\.cs$"), normalized);
                Assert.DoesNotMatch(new Regex(@"GlobalUsings"), normalized);
                Assert.DoesNotMatch(new Regex(@"AssemblyInfo"), normalized);
                Assert.DoesNotMatch(new Regex(@"/obj/"), normalized);
            }

            // Sanity: the fold DOES reach both in-scope projects.
            Assert.Contains(lines, l => l.Replace('\\', '/').Contains("/BaseProcessor.Core/"));
            Assert.Contains(lines, l => l.Replace('\\', '/').Contains("/Processor.Sample/"));
        }
        finally
        {
            if (File.Exists(dump)) File.Delete(dump);
        }
    }

    /// <summary>Walks up from the test output dir to the repo root, then to src/Processor.Sample.</summary>
    private static string LocateProcessorSampleProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SK_P.sln")))
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate repo root (SK_P.sln) from test output dir.");
        var projectDir = Path.Combine(dir!.FullName, "src", "Processor.Sample");
        Assert.True(Directory.Exists(projectDir), $"Processor.Sample project dir not found at {projectDir}.");
        return projectDir;
    }
}
