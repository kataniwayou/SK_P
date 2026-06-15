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
        // Phase 62 (D-01) — processor-sample was reshaped to a 2-replica tier mirroring the
        // keeper: `container_name` was DELETED (a named container cannot scale) and
        // `deploy.replicas: 2` added. Assert the build context + the replica directive,
        // block-scoped via the same TEMPERED-greedy window the keeper guard uses (`(?:(?!^  \S).)*?`
        // refuses to cross a `^  \S` top-level service header) so a neighbouring tier's deploy:
        // block can never false-pass.
        Assert.Matches(new Regex(@"dockerfile:\s*src/Processor\.Sample/Dockerfile"), content);
        Assert.Matches(new Regex(@"(?ms)^  processor-sample:(?:(?!^  \S).)*?deploy:\s*\n\s*replicas:\s*2"), content);
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
    public void ComposeYaml_ProcessorSample_Sets_ExecutionDataTtl_To_ProductionDefault()
    {
        var content = ComposeYamlContent();
        // ExecutionDataTtl matches the appsettings production default (300s) so the L2[entryId] data key
        // and the L2[messageId] index (now the SAME const, quick task 260615-dbf) survive a fault dwell.
        // The former "5" was a v6.0.0 close-gate net-zero hack, obsolete once v8.0.0 retired that gate —
        // it caused the Phase 68 TEST-06 desync artifact (data expired at 5s vs the 45s outage).
        Assert.Matches(new Regex(@"(?ms)processor-sample:[\s\S]*?Processor__ExecutionDataTtl:\s*""300"""), content);
    }

    // ---- Phase 34 (KEEP-03) — keeper service block guards (block-scoped) ----

    [Fact]
    public void ComposeYaml_Has_Keeper_Service_Block()
    {
        var content = ComposeYamlContent();
        // KEEP-03 — the keeper tier builds from the Keeper Dockerfile and probes 8083.
        Assert.Matches(new Regex(@"dockerfile:\s*src/Keeper/Dockerfile"), content);
        Assert.Matches(new Regex(@"http://localhost:8083/health/ready"), content);
    }

    [Fact]
    public void ComposeYaml_Keeper_Declares_Two_Replicas()
    {
        var content = ComposeYamlContent();
        // D-04 — deploy.replicas: 2, scoped to the keeper block via a TEMPERED-greedy window
        // `(?:(?!^  \S).)*?` that refuses to cross a `^  \S` top-level service header, so a
        // neighbouring tier's deploy: block could never false-pass.
        Assert.Matches(new Regex(@"(?ms)^  keeper:(?:(?!^  \S).)*?deploy:\s*\n\s*replicas:\s*2"), content);
    }

    [Fact]
    public void ComposeYaml_Keeper_Has_No_ContainerName()
    {
        var content = ComposeYamlContent();
        // D-04 — a named container cannot scale. The file's OTHER tiers (orchestrator,
        // processor-sample, redis...) legitimately set container_name, so a plain block-prefix
        // `[\s\S]*?` would walk PAST the keeper block into a neighbour and FALSE-PASS (verified —
        // it returns a match against the real file). The TEMPERED-greedy token
        // `(?:(?!^  \S).)*?` matches only chars that do NOT begin a new `^  \S` service header,
        // bounding the window to the keeper block. Empirically confirmed: NO match on the real
        // keeper block (assertion passes) AND a match if a container_name line is planted inside
        // the keeper block (regression caught).
        Assert.DoesNotMatch(
            new Regex(@"(?ms)^  keeper:(?:(?!^  \S).)*?container_name:"),
            content);
    }

    [Fact]
    public void ComposeYaml_Keeper_Has_No_BaseApi_Dependency()
    {
        var content = ComposeYamlContent();
        // D-05 — Keeper resolves no identity over the WebApi; the keeper block must NOT depend on
        // baseapi-service (which IS a real depends_on / service of other tiers). Same tempered-greedy
        // block-scoping as the container_name fact above (a plain prefix-glob would cross into the
        // processor-sample / baseapi-service tiers and false-pass).
        Assert.DoesNotMatch(
            new Regex(@"(?ms)^  keeper:(?:(?!^  \S).)*?baseapi-service:"),
            content);
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
