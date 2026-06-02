using System.Text.RegularExpressions;
using Xunit;

namespace BaseApi.Tests.Composition;

/// <summary>
/// Phase 12 INFRA-REDIS-01..02 — compose.yaml file-content regex assertions for the
/// redis service block (D-01..D-03), the healthcheck shape (CMD form per Pitfall 5),
/// and the baseapi-service depends_on + environment additions (D-04). These are the
/// CI-enforceable guard rails — a regression that drops the image pin, the SAVE/AOF
/// disable, or the dependency wiring surfaces as a test failure.
/// </summary>
[Trait("Phase12Wave", "C")]
public sealed class ComposeYamlFacts
{
    private static string ComposeYamlContent()
    {
        var path = Path.Combine(FindRepoRoot(), "compose.yaml");
        Assert.True(File.Exists(path), $"compose.yaml not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void ComposeYaml_Has_Redis_Service_Block()
    {
        var content = ComposeYamlContent();
        Assert.Contains("container_name: sk-redis", content);   // D-02
    }

    [Fact]
    public void ComposeYaml_Pins_Redis_7_4_Alpine()
    {
        var content = ComposeYamlContent();
        // INFRA-REDIS-01 — must be 7.4.x-alpine (RSALv2/SSPLv1), NOT 8.0+ (AGPLv3).
        Assert.Matches(new Regex(@"image:\s*redis:7\.4\.\d+-alpine"), content);
    }

    [Fact]
    public void ComposeYaml_Maps_Redis_6380_to_6379()
    {
        var content = ComposeYamlContent();
        Assert.Contains("\"6380:6379\"", content);              // D-01 + D-13
    }

    [Fact]
    public void ComposeYaml_Disables_Redis_Persistence()
    {
        var content = ComposeYamlContent();
        Assert.Contains("\"--save\", \"\"", content);           // D-03 — no RDB
        Assert.Contains("\"--appendonly\", \"no\"", content);   // D-03 — no AOF
    }

    [Fact]
    public void ComposeYaml_Has_Redis_PING_Healthcheck_CMD_Form()
    {
        var content = ComposeYamlContent();
        // Pitfall 5 — CMD form (NOT CMD-SHELL) avoids Alpine BusyBox quoting hazard.
        Assert.Contains("\"CMD\", \"redis-cli\", \"ping\"", content);
    }

    [Fact]
    public void ComposeYaml_Healthcheck_Cadence_Matches_INFRA_REDIS_02()
    {
        var content = ComposeYamlContent();
        // INFRA-REDIS-02 cadence: 5s/3s/10/5s. Anchor on the redis-cli ping healthcheck
        // `test:` directive (NOT the redis service `image:` line) — the verbose D-01..D-03
        // comment block between the service header and the cadence keys pushes them well
        // past a 500-char window from `image:`, so we pin to the unique redis-cli `test:`
        // line and walk forward to the cadence keys that follow it.
        var afterTest = new Regex(@"test:\s*\[\s*""CMD""\s*,\s*""redis-cli""\s*,\s*""ping""\s*\]([\s\S]{0,300})");
        var m = afterTest.Match(content);
        Assert.True(m.Success, "redis-cli ping healthcheck `test:` directive not found in compose.yaml");
        var cadence = m.Groups[1].Value;
        Assert.Matches(new Regex(@"interval:\s*5s"), cadence);
        Assert.Matches(new Regex(@"timeout:\s*3s"), cadence);
        Assert.Matches(new Regex(@"retries:\s*10"), cadence);
        Assert.Matches(new Regex(@"start_period:\s*5s"), cadence);
    }

    [Fact]
    public void ComposeYaml_BaseApi_DependsOn_Redis_Healthy()
    {
        var content = ComposeYamlContent();
        // INFRA-REDIS-02 — baseapi-service.depends_on includes redis: condition: service_healthy.
        Assert.Matches(new Regex(@"(?ms)depends_on:[\s\S]*?redis:\s+condition:\s+service_healthy"), content);
    }

    [Fact]
    public void ComposeYaml_BaseApi_Env_Includes_ConnectionStrings_Redis()
    {
        var content = ComposeYamlContent();
        // D-04 — Docker-internal hostname; defensive double-underscore convention.
        Assert.Contains(
            "ConnectionStrings__Redis: \"redis:6379,abortConnect=false,connectTimeout=5000\"",
            content);
    }

    [Fact]
    public void ComposeYaml_No_Redis_Persistence_Volume()
    {
        var content = ComposeYamlContent();
        // D-03 — no named volume for redis (would re-enable persistence implicitly via
        // some other operator mistake later).
        Assert.DoesNotContain("redisdata:", content);
    }

    [Fact]
    public void ComposeYaml_Does_NOT_Pin_Redis_8x()
    {
        var content = ComposeYamlContent();
        // INFRA-REDIS-01 — Redis 8.0+ adds AGPLv3 network-distribution copyleft.
        Assert.DoesNotMatch(new Regex(@"image:\s*redis:8\."), content);
    }

    [Fact]
    public void ComposeYaml_Does_NOT_Use_CmdShell_For_Redis_Healthcheck()
    {
        var content = ComposeYamlContent();
        // Pitfall 5 — CMD-SHELL form interacts badly with Alpine BusyBox sh quoting.
        // Assert the redis healthcheck uses a CMD-form `test:` directive carrying redis-cli,
        // and that NO `test: ["CMD-SHELL", ...]` directive references redis-cli anywhere.
        // (The redis service comment block deliberately mentions the string "CMD-SHELL" in
        //  prose — "CMD form (NOT CMD-SHELL)" — so a naive substring/window scan around the
        //  redis block yields a false positive. We match the actual `test:` array directive
        //  instead of comment prose.)
        Assert.DoesNotMatch(
            new Regex(@"test:\s*\[\s*""CMD-SHELL""[^\]]*redis-cli"),
            content);
    }

    // ---- Phase 28 (SAMPLE-02) — processor-sample service block guards ----

    [Fact]
    public void ComposeYaml_Has_ProcessorSample_Service_Block()
    {
        var content = ComposeYamlContent();
        Assert.Contains("container_name: sk-processor-sample", content);
        Assert.Matches(new Regex(@"dockerfile:\s*src/Processor\.Sample/Dockerfile"), content);
    }

    [Fact]
    public void ComposeYaml_ProcessorSample_DependsOn_BaseApi_Healthy()
    {
        var content = ComposeYamlContent();
        // The processor resolves identity over the bus FROM the WebApi responder — it must wait for
        // baseapi-service to be healthy. The on-disk compose uses the multi-line condition: form.
        Assert.Matches(
            new Regex(@"(?ms)processor-sample:[\s\S]*?baseapi-service:\s*\n\s*condition:\s*service_healthy"),
            content);
    }

    [Fact]
    public void ComposeYaml_ProcessorSample_Sets_Short_ExecutionDataTtl()
    {
        var content = ComposeYamlContent();
        // Pitfall 4 — short TTL so the round-trip's skp:data:* keys self-expire before the
        // close-gate AFTER snapshot.
        Assert.Matches(new Regex(@"(?ms)processor-sample:[\s\S]*?Processor__ExecutionDataTtl:\s*""5"""), content);
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
