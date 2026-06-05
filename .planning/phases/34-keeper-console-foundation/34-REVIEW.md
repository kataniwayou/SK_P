---
phase: 34-keeper-console-foundation
reviewed: 2026-06-05T00:00:00Z
depth: standard
files_reviewed: 16
files_reviewed_list:
  - src/Messaging.Contracts/KeeperQueues.cs
  - src/Keeper/Keeper.csproj
  - src/Keeper/Program.cs
  - src/Keeper/appsettings.json
  - src/Keeper/Dockerfile
  - src/Keeper/Consumers/KeeperPlaceholder.cs
  - src/Keeper/Consumers/PlaceholderConsumer.cs
  - src/Keeper/Consumers/PlaceholderConsumerDefinition.cs
  - compose.yaml
  - SK_P.sln
  - tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs
  - tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs
  - tests/BaseApi.Tests/Keeper/KeeperHostBootTests.cs
  - tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs
  - tests/BaseApi.Tests/BaseApi.Tests.csproj
findings:
  critical: 0
  warning: 2
  info: 2
  total: 4
status: issues_found
---

# Phase 34: Code Review Report

**Reviewed:** 2026-06-05T00:00:00Z
**Depth:** standard
**Files Reviewed:** 16
**Status:** issues_found

## Summary

Phase 34 delivers a clean Generic-Host shell for Keeper that correctly mirrors the Orchestrator pattern. The MassTransit competing-consumer topology (plain `AddConsumer` + stable `EndpointName`, no `InstanceId`/`Temporary` override), the Compose multi-replica block (no `container_name`, `deploy.replicas: 2`), the Dockerfile aspnet:8.0 runtime with wget, and the reference firewall (`BaseConsole.Core` + `Messaging.Contracts` only) are all implemented correctly. The no-op placeholder consumer is deliberately throwaway and is not flagged.

Two warnings are raised: one is a real but latent bug in `PlaceholderConsumerDefinition` (the `Strategy` field of `RetryOptions` is bound from config but never read — `Immediate` is always hard-wired regardless of configuration), and one is a test reliability limitation in `KeeperDependencyFirewallTests` (shallow reflection). Two info items cover the test config `Retry` section gap and the appsettings dev credentials.

## Warnings

### WR-01: `RetryOptions.Strategy` Is Bound From Config But Always Silently Ignored

**File:** `src/Keeper/Consumers/PlaceholderConsumerDefinition.cs:32`

**Issue:** `ConfigureConsumer` calls `endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit))` — it reads `Limit` from config but unconditionally hard-wires `Immediate`, ignoring `_retryOptions.Value.Strategy` entirely. The `RetryOptions` type supports three strategy variants (`Immediate`, `Interval`, `Exponential`), `appsettings.json` binds `"Strategy": "Immediate"`, and `Program.cs` calls `Configure<RetryOptions>(GetSection("Retry"))` — creating the expectation that changing `Strategy` in config has an effect. It does not. Any operator who sets `Strategy: Interval` in a deployment override will see no change in retry behavior, with no error or warning.

**Note:** This same pattern exists in the Orchestrator consumer definitions — it is not newly introduced by Keeper. The project doc comment on `RetryOptions.Strategy` explicitly says "NOT wired (Phase 31 out-of-scope-as-default)". This is therefore a known intentional deferral, not an accidental omission. It is flagged here because the binding+ignore combination is a latent correctness hazard for Phase 35/36 consumers that "inherit this pattern" (per the `Program.cs` comment). If the strategy remains permanently unread, the `Strategy` field and its config binding should be removed or a branch added before Phase 35 consumers land.

**Fix:**
Either remove the `Strategy` field from `RetryOptions` and the config key until it is actually wired, or add a switch on strategy at the point of use:

```csharp
protected override void ConfigureConsumer(
    IReceiveEndpointConfigurator endpointConfigurator,
    IConsumerConfigurator<PlaceholderConsumer> consumerConfigurator,
    IRegistrationContext context)
{
    var limit = _retryOptions.Value.Limit;
    endpointConfigurator.UseMessageRetry(r =>
    {
        _ = _retryOptions.Value.Strategy switch
        {
            RetryStrategy.Immediate  => r.Immediate(limit),
            RetryStrategy.Interval   => r.Interval(limit, TimeSpan.FromSeconds(1)),
            RetryStrategy.Exponential => r.Exponential(limit, TimeSpan.FromSeconds(1),
                                             TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)),
            _ => r.Immediate(limit)
        };
    });
}
```

Or, if the deferral is explicit and intentional until Phase 35, add a `// Phase 35: wire Strategy` comment and remove the `Strategy` config key from `appsettings.json` so the disconnect is not silently misleading.

---

### WR-02: `KeeperDependencyFirewallTests` Reflects Only Direct References — Transitive Leaks Are Not Caught

**File:** `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs:34`

**Issue:** `KeeperAssembly.GetReferencedAssemblies()` returns only the **direct** assembly references recorded in `Keeper.dll`'s manifest — it does not walk the transitive closure. A future dependency added to `BaseConsole.Core` or `Messaging.Contracts` that pulls in `BaseApi.Core` or `Npgsql` transitively would not trip this guard. The firewall test name says "reference closure" but it only enforces the direct layer. The plan comment acknowledges this as a reflection-level guard, but the gap means a future prohibited reference introduced via an intermediate assembly would be silently missed.

**Fix:** Either document the known gap explicitly in the test summary ("direct references only — transitive closure requires a build-task scan"), or replace `GetReferencedAssemblies()` with a recursive walk:

```csharp
private static IEnumerable<string> GetAllReferencedAssemblyNames(Assembly root)
{
    var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var queue   = new Queue<AssemblyName>(root.GetReferencedAssemblies());
    while (queue.Count > 0)
    {
        var name = queue.Dequeue().Name ?? string.Empty;
        if (!visited.Add(name)) continue;
        yield return name;
        try
        {
            var loaded = Assembly.Load(name);
            foreach (var child in loaded.GetReferencedAssemblies())
                queue.Enqueue(child);
        }
        catch { /* skip assemblies not in the load context */ }
    }
}
```

If the recursive approach is deferred, add a `// NOTE: direct references only` annotation to the test so future reviewers understand the guard's actual depth.

---

## Info

### IN-01: Test Fixture Does Not Inject a `Retry` Config Section — `RetryOptions` Resolves to Defaults Silently

**File:** `tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs:29`

**Issue:** `KeeperHostBootFixture.ConfigureBuilder` calls `builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"))`, but the inherited `ConsoleTestHostFixture.BuildConfig()` dictionary does not include any `"Retry:Limit"` or `"Retry:Strategy"` keys. When the section is absent, `IOptions<RetryOptions>` resolves with the property initializer defaults (`Limit = 3`, `Strategy = Immediate`). The boot test passes because `PlaceholderConsumerDefinition` reads `.Limit` and `3` is a valid value. However, if `RetryOptions` gains a required property or a validated range in a future phase, the missing config section would fail silently in the boot fixture while the live service has the correct value.

**Fix:** Add the `Retry` section to `BuildConfig` in the fixture (or override it locally in `KeeperHostBootFixture`) to mirror the live `appsettings.json`:

```csharp
// In KeeperHostBootFixture.ConfigureBuilder, after AddBaseConsole:
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Retry:Limit"]    = "3",
    ["Retry:Strategy"] = "Immediate",
});
builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));
```

---

### IN-02: Hardcoded `guest/guest` RabbitMQ Credentials in `appsettings.json`

**File:** `src/Keeper/appsettings.json:22-24`

**Issue:** `RabbitMq.Username` and `RabbitMq.Password` are `guest/guest` in the committed `appsettings.json`. This is the established project dev posture (Orchestrator and Processor.Sample carry the same values), explicitly documented as "T-34-07 — guest/guest dev-only posture; k8s/prod override via secrets". No action required for dev, but prod deployments must override via environment variables (`RabbitMq__Username` / `RabbitMq__Password`) or a secrets provider. The pattern is consistent with the rest of the stack.

**Fix:** No immediate action. Consider adding a comment to `appsettings.json` parallel to the compose comment (`# T-34-07 dev-only; prod overrides via env/secrets`) to make the intent self-documenting at the file level rather than only in the compose comment.

---

_Reviewed: 2026-06-05T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
