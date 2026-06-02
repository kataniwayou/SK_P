---
phase: 30-runtime-business-metrics
reviewed: 2026-06-02T20:57:07Z
depth: standard
files_reviewed: 17
files_reviewed_list:
  - src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs
  - src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs
  - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
  - src/BaseProcessor.Core/Observability/ProcessorMetrics.cs
  - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
  - src/Orchestrator/Consumers/ResultConsumer.cs
  - src/Orchestrator/Dispatch/StepDispatcher.cs
  - src/Orchestrator/Observability/OrchestratorMetrics.cs
  - src/Orchestrator/Program.cs
  - tests/BaseApi.Tests/Observability/Helpers/PrometheusTestClient.cs
  - tests/BaseApi.Tests/Observability/ResolveInstanceIdFacts.cs
  - tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs
  - tests/BaseApi.Tests/Orchestrator/OrchestratorTestStubs.cs
  - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
  - tests/BaseApi.Tests/Processor/FakeProcessorContext.cs
  - tests/BaseApi.Tests/Processor/ProcessorMetricsFacts.cs
findings:
  critical: 0
  warning: 1
  info: 4
  total: 5
status: issues_found
---

# Phase 30: Code Review Report

**Reviewed:** 2026-06-02T20:57:07Z
**Depth:** standard
**Files Reviewed:** 17
**Status:** issues_found

## Summary

Phase 30 wires OpenTelemetry runtime and business metrics: a per-replica
`service.instance.id` resource attribute applied to both the logs and metrics
resources, two code-owned `Meter`s (`Orchestrator` and `BaseProcessor`) each
holding two monotonic `Counter<long>` instruments, and the Prometheus
round-trip E2E proof plus hermetic guards.

Overall this is high-quality, well-reasoned work. The load-bearing correctness
properties the phase set out to guarantee all hold:

- **Counter-increment placement is correct.** `orchestrator_dispatch_sent` and
  `processor_result_sent` are incremented strictly AFTER the confirmed
  `endpoint.Send` (StepDispatcher.cs:27,33; EntryStepDispatchConsumer.cs:222-225),
  so an infra Send fault propagates before the counter moves — only confirmed
  sends count. The two "consumed" counters increment at the top of `Consume`
  on purpose so the graceful L1-miss / business-ack paths are still counted.
- **Label cardinality is bounded.** No `workflowId` label anywhere; tags are
  `ProcessorId` (a Guid, bounded by the processor population) plus, on
  `processor_result_sent`, `outcome` which is provably ∈ {completed, failed,
  cancelled} — `StepOutcome` has only 4 values and the builders never emit
  `Processing` (verified against `Messaging.Contracts/StepOutcome.cs`).
- **Thread-safety holds.** Both meter holders are DI singletons built from
  `IMeterFactory` (never a `static Meter`), and `Counter<T>.Add` is documented
  thread-safe — concurrent consumers incrementing the shared singleton is safe.
- **The `BaseProcessor.Core → BaseConsole.Core` firewall is intact.** Verified
  via the `.csproj` files: `BaseConsole.Core` references only
  `Messaging.Contracts`; `BaseProcessor.Core` references `BaseConsole.Core` +
  `Messaging.Contracts`. The `AddMeter(ProcessorMetrics.MeterName)` registration
  correctly lives in `AddBaseProcessor` (BaseProcessor.Core), not in the shared
  `AddBaseConsoleObservability`, so the one-way dependency is preserved.
- **PromQL-injection surface is mitigated.** `PrometheusTestClient` routes every
  query through `Uri.EscapeDataString` (lines 83, 210).

The findings below are all minor (one Warning, four Info). None blocks the phase.

## Warnings

### WR-01: Instance-id resolution can diverge across processes / containers in the round-trip E2E label assertion

**File:** `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs:149-156`
**Issue:** The test asserts that a `process_runtime_dotnet_*` series carries a
non-empty `service_instance_id` (METRIC-01/02). The runtime instrumentation
series is produced by whichever container's SDK emitted it (orchestrator or
processor-sample), and `service.instance.id` resolves per-process via
`POD_NAME → HOSTNAME → MachineName → GUID`. In a compose/CI environment where
no `POD_NAME`/`HOSTNAME` is set, the value falls through to `MachineName`,
which for multiple containers on the same Docker host can collide (same
`MachineName`) — the assertion still passes (it only checks non-empty), so this
is not a test bug today, but the doc comment's framing ("a RUNTIME metric
carries a non-empty `service_instance_id`") understates that the *uniqueness*
property the resource attribute is meant to provide is NOT asserted and is not
guaranteed under the `MachineName` fallback.
**Fix:** No code change required for correctness. If per-replica uniqueness is a
real acceptance goal (the resource attribute's whole purpose), consider either
(a) documenting that compose sets `HOSTNAME` per container (Docker does set the
container id as `HOSTNAME` by default, which would make this hold) so the
fallback to `MachineName` is not actually reached, or (b) adding an assertion
that distinct replicas carry distinct `service_instance_id` values when more
than one replica is exercised. At minimum, add a one-line note to the test that
non-emptiness — not uniqueness — is what is being proven here.

## Info

### IN-01: `?? Guid.NewGuid()` fallback in `ResolveInstanceId` is unreachable

**File:** `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs:94-98`
**Also:** `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:85-89`
**Issue:** `Environment.MachineName` never returns `null` (it throws
`InvalidOperationException` if the name cannot be read, per the BCL contract),
so the trailing `?? Guid.NewGuid().ToString("N")` branch is dead code. The
inline comment ("MachineName is effectively non-null; GUID is the documented
final fallback") already acknowledges this. This is an intentional,
documented defensive fallback, not a defect — flagged only for completeness.
**Fix:** None required. The dead branch is harmless and self-documenting. If a
truly-never-empty guarantee is wanted, `MachineName` could be wrapped in a
try/catch that maps the throw to the GUID, but that is gold-plating.

### IN-02: GUID interpolated into PromQL label selector without `Uri.EscapeDataString`-equivalent validation

**File:** `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs:117-138`
**Issue:** The PromQL strings interpolate `{procId:D}` directly into the label
selector (e.g. `orchestrator_dispatch_sent_total{{ProcessorId="{procId:D}"}}`)
before the whole query is passed to `PollPromForQuery`, which then
`Uri.EscapeDataString`s the entire string. The transport-layer escaping is
correct, but the value is interpolated into the PromQL *grammar* (inside a
quoted label matcher) without any grammar-level escaping. This is safe here
because `procId` is a server-minted `Guid` rendered with the `D` format
(hex + hyphens only — no `"`, `{`, `}`, `\`), so it cannot break out of the
label-matcher string. Flagged so the safety reasoning is recorded: the
"only a validated GUID procId interpolated" claim in
`PrometheusTestClient.PollPromForQuery`'s doc comment (line 69) is the actual
invariant keeping this safe, and it holds.
**Fix:** None required. If non-GUID values were ever interpolated into a PromQL
label matcher, they would need PromQL-string escaping (backslash-escape `"` and
`\`) in addition to the existing URL escaping.

### IN-03: Duplicated `ResolveInstanceId` + duplicated `Metrics()` test factory — accepted duplication, but drift-prone

**File:** `src/BaseApi.Core/.../ObservabilityServiceCollectionExtensions.cs:94`,
`src/BaseConsole.Core/.../BaseConsoleObservabilityExtensions.cs:85`,
`tests/BaseApi.Tests/Observability/ResolveInstanceIdFacts.cs:34`
**Issue:** The `POD_NAME ?? HOSTNAME ?? MachineName ?? GUID` precedence
expression exists in three places (two production copies + one test mirror).
The test file's doc comment explicitly notes it "must stay byte-for-byte
equivalent." This is a deliberate D-09 decision (the firewall forbids a shared
lib reference and the helper is too small to justify one), and the test exists
precisely to catch precedence drift — but the test mirror is itself a hand copy,
so a change to all three in lock-step is required and nothing mechanically
enforces it. Same pattern with the duplicated `Metrics()` factory in
`OrchestratorTestStubs.cs:106` and `DispatchTestKit.cs:126`.
**Fix:** None required — the tradeoff is sound and documented. Optionally, add a
single comment cross-reference (file + line) in each copy pointing at the other
two so a future editor finds all three. The test mirror already does this in
prose.

### IN-04: `outcome` tag value re-computes `ToString().ToLowerInvariant()` on every send

**File:** `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:225`
**Issue:** `result.Outcome.ToString().ToLowerInvariant()` allocates an enum name
string + a lowercased copy on every result sent. This is a per-message hot path.
Not a correctness or cardinality issue (the value set is bounded to 3), and
performance is explicitly out of v1 review scope — flagged only as a
maintainability note. A small static map (`StepOutcome → string`) would remove
the allocation and also make the exact emitted label values explicit at one
site (guarding against an enum rename silently changing a Prometheus label).
**Fix:** Optional. Replace with a `switch` expression or a precomputed
`static readonly` lookup returning the literal `"completed"/"failed"/"cancelled"`
strings, which also pins the label vocabulary independent of the C# enum member
names.

---

_Reviewed: 2026-06-02T20:57:07Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
