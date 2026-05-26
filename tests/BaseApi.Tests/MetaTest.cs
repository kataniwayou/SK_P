using Xunit;

namespace BaseApi.Tests;

/// <summary>
/// Sanity test — proves the xUnit v3 test stack wires correctly end-to-end:
///   - BaseApi.Tests.csproj resolves xunit.v3, xunit.v3.assert, and
///     xunit.runner.visualstudio from Directory.Packages.props
///   - The test runner discovers [Fact]-decorated methods
///   - Assert.True executes against the v3 assertion library
///
/// Required by Phase 1 CONTEXT.md D-11. Future regressions in test wiring
/// (NuGet pin drift, runner package missing, .NET 8 SDK mismatch) surface as a
/// failure of this test, not as an opaque "no tests discovered" runner output.
///
/// This file is the only test in Phase 1. Phases 3-8 add real unit and
/// integration tests under Phase-specific folders inside this project.
/// </summary>
public sealed class MetaTest
{
    [Fact]
    public void Sanity() => Assert.True(true);
}
