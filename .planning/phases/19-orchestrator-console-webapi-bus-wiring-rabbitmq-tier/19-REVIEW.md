---
phase: 19-orchestrator-console-webapi-bus-wiring-rabbitmq-tier
reviewed: 2026-05-30T00:00:00Z
depth: standard
files_reviewed: 28
files_reviewed_list:
  - src/Messaging.Contracts/ICorrelated.cs
  - src/Messaging.Contracts/StartOrchestration.cs
  - src/Messaging.Contracts/StopOrchestration.cs
  - src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs
  - src/BaseApi.Core/BaseApi.Core.csproj
  - src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs
  - src/BaseApi.Service/Program.cs
  - src/BaseApi.Service/appsettings.json
  - src/BaseApi.Service/Properties/AssemblyInfo.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs
  - src/BaseApi.Service/Features/Orchestration/OrchestrationServiceCollectionExtensions.cs
  - src/Orchestrator/Program.cs
  - src/Orchestrator/Orchestrator.csproj
  - src/Orchestrator/appsettings.json
  - src/Orchestrator/Dockerfile
  - src/Orchestrator/Consumers/StartOrchestrationConsumer.cs
  - src/Orchestrator/Consumers/StopOrchestrationConsumer.cs
  - src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs
  - src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs
  - src/Orchestrator/Consumers/WorkflowRootNotFoundException.cs
  - src/Orchestrator/Messaging/OrchestratorL2Keys.cs
  - src/Orchestrator/Messaging/OrchestratorRedisOptions.cs
  - compose.yaml
  - SK_P.sln
  - Directory.Packages.props
  - tests/BaseApi.Tests/Console/ConsoleCorrelationFilterTests.cs
  - tests/BaseApi.Tests/Orchestration/OrchestrationServicePublishTests.cs
  - tests/BaseApi.Tests/Orchestrator/StartStopConsumerAckTests.cs
  - tests/BaseApi.Tests/BaseApi.Tests.csproj
findings:
  critical: 0
  warning: 2
  info: 5
  total: 7
status: issues_found
---

# Phase 19: Code Review Report

**Reviewed:** 2026-05-30
**Depth:** standard
**Files Reviewed:** 28
**Status:** issues_found

## Summary

Phase 19 wires the RabbitMQ tier: a publish-only MassTransit join in the WebApi
(`AddBaseApiMessaging`), the `StartOrchestration`/`StopOrchestration` control records,
two orchestrator consumers with a per-replica fan-out endpoint, the broker compose
service, and harness-only tests. The code is clean, heavily documented, and the
design decisions (body-carried correlation, business-ack vs infra-throw split,
publish-only firewall, soft-vs-hard broker health posture) are coherent and
well-justified inline. No security vulnerabilities, no crashes, no injection vectors.

Two warnings concern silent failure modes that the harness tests do not catch and
that could surface as data anomalies in production. The info items are minor
consistency and dead-code observations. Nothing blocks merge.

## Warnings

### WR-01: Stop publishes `StopOrchestration` even when zero workflows pass the gate

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:240-242` (and Start `163-165`)
**Issue:** `StopAsync` validates the ids (which presumably rejects an empty list via
`WorkflowIdsValidator`), so the empty-input case is likely guarded. However the
control message is built from `workflowIds.ToArray()` — the ORIGINAL request list —
not from the set that actually passed each per-workflow stage. In `StartAsync`, the
publish at line 163 always carries the full input list even though individual
workflows could in principle be skipped by future logic; today every id is processed
or the method throws, so the published set is currently faithful. The concern is
forward-fragility: if a future change makes any per-workflow step `continue`-on-skip
(mirroring the consumer's business-skip pattern), the published `WorkflowIds` would
silently over-claim what was projected. The consumers then iterate that list and
business-skip the missing ones — masking the divergence.
**Fix:** Publish the set that actually completed, not the raw input, so the contract
stays truthful under future edits:
```csharp
var projected = new List<Guid>();
foreach (var workflowId in workflowIds)
{
    // ... existing per-workflow pipeline ...
    projected.Add(workflowId);
}
await _publishEndpoint.Publish(
    new StartOrchestration(projected.ToArray()) { CorrelationId = NewId.NextGuid() }, ct);
```
If the input-list semantics are intentional and locked, add an explicit comment
stating "publish the full request list by design" so the coupling is documented.

### WR-02: Start publish failure leaves L2 written but no orchestration scheduled (partial-success window)

**File:** `src/BaseApi.Service/Features/Orchestration/OrchestrationService.cs:115-165`
**Issue:** `StartAsync` writes every workflow root + per-step keys to Redis L2 inside
the loop (lines 115-154), THEN publishes `StartOrchestration` once at the end
(line 163). The inline comment correctly notes the publish failure propagates → 500.
But by that point all L2 writes have already committed. A broker-unreachable publish
therefore yields: caller sees 500, L2 holds fully-projected roots, yet no
`StartOrchestration` ever reaches the orchestrator. A client retry re-runs the
tolerant pre-clean + re-projects + re-publishes, so it is eventually consistent IF
the client retries — but a non-retrying client leaves orphaned-but-unscheduled L2
state with a 500 that gives no hint that the projection half-succeeded. This is a
genuine atomicity gap, not just a style issue.
**Fix:** This is inherent to dual-write (Redis + broker) without an outbox and may be
an accepted Phase 19 limitation. At minimum, document the partial-success contract on
`StartAsync` (caller MUST retry on 500; retry is idempotent via pre-clean) so the
recovery expectation is explicit. The robust fix (outbox / transactional publish) is
likely a later phase — if so, leave a `// TODO(phase-N): outbox to close Start
dual-write window` marker referencing the decision.

## Info

### IN-01: `WorkflowRootNotFoundException` is never thrown — `Ignore<>` guard is currently dead

**File:** `src/Orchestrator/Consumers/WorkflowRootNotFoundException.cs:8`,
`src/Orchestrator/Consumers/StartOrchestrationConsumer.cs:34-40`,
`src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs:24` (and Stop mirror)
**Issue:** Both consumers handle absent-from-L2 by logging a warning and `continue`
(line 37-40) — they never construct or throw `WorkflowRootNotFoundException`. The
type is referenced ONLY by the definitions' `r.Ignore<WorkflowRootNotFoundException>()`.
So the exception class and the `Ignore<>` registration are both presently
unreachable. The XML doc and inline comment frame it as a defensive guard against an
"escaped business exception," which is a reasonable forward-looking posture, but as
written it is dead code that no test exercises.
**Fix:** Acceptable as a defensive seam if intentional — but make the intent
unmistakable, e.g. comment `// defensive: no current path throws this; guard exists so
a FUTURE throw can never retry-storm`. Otherwise consider deferring the type until a
code path actually throws it.

### IN-02: `StopOrchestrationConsumer` seam log says "Scheduler job start" — copy-paste artifact

**File:** `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs:42`
**Issue:** The Stop consumer logs `"Scheduler job start (seam) for {WorkflowId}"` —
identical to the Start consumer. For a Stop message the seam is a job STOP/cancel, so
the message is semantically wrong and would confuse log readers. The test
(`StartStopConsumerAckTests` line 217) asserts `m.Contains("Scheduler job start")`,
which means the copy-paste was carried into the assertion too.
**Fix:** Use a Stop-appropriate template, e.g.
`logger.LogInformation("Scheduler job stop (seam) for {WorkflowId}", workflowId);`
and update the Stop test assertion to match (`"Scheduler job stop"`). The class XML
doc on line 13 also reads "logs to the scheduler-job-start seam" — adjust likewise.

### IN-03: Deserialized L2 projection is discarded (`_ = ...`) — read is a no-op beyond existence

**File:** `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs:43`,
`src/Orchestrator/Consumers/StopOrchestrationConsumer.cs:39`
**Issue:** `_ = JsonSerializer.Deserialize<WorkflowRootProjection>(raw!)` parses the
value then throws it away. Today this only serves to validate that the stored JSON is
well-formed (a malformed body would throw `JsonException` → infra-style fault → retry
→ _error). That is a legitimate effect, but it is subtle: a reader may think the
deserialize is dead. Note `JsonException` is NOT a `RedisException`, so a poison
(malformed) L2 value would be treated as an infra fault and retried 3× before
dead-lettering — which is reasonable but undocumented.
**Fix:** Add a one-line comment clarifying the discard is an intentional well-formed
validation, and note the poison-message behavior, e.g.
`// validate shape only (ORCH-CON-04 seam, no consumption yet); a malformed body
throws JsonException → retry → _error`. No functional change needed.

### IN-04: Service version strings differ across apps without a shared source

**File:** `src/BaseApi.Service/appsettings.json:11` (`"Version": "3.2.0"`) vs
`src/Orchestrator/appsettings.json:11` (`"Version": "3.4.0"`)
**Issue:** The two services carry hand-maintained version literals that have already
drifted (3.2.0 vs 3.4.0). These are magic strings duplicated per-app with no single
source; they will keep drifting as phases land. Not a bug, but a maintenance smell for
observability (the version attribute feeds OTel resource tags).
**Fix:** Consider sourcing the service version from assembly metadata
(`Assembly.GetEntryAssembly().GetName().Version` or an MSBuild `<Version>` property)
rather than a literal in each appsettings, or document why the two are intentionally
independent.

### IN-05: Dockerfile installs `wget` at runtime via apt — adds layer + network dependency to the image build

**File:** `src/Orchestrator/Dockerfile:28-30`
**Issue:** The `apt-get install wget` step (added as the documented fix(19-04) for the
distroless-runtime healthcheck) makes the runtime image depend on Debian package repos
at build time and grows the image. The comment justifies it well. An alternative used
elsewhere in this repo (the otel-collector service, compose.yaml lines 69-79) is to
drop the in-container healthcheck and probe from the host instead. Not a defect — the
chosen approach is consistent with the redis/prometheus/baseapi `wget --spider` idiom.
**Fix:** No change required. Optionally, a self-contained probe (a tiny dotnet
`HEALTHCHECK` invoking the app's own `/health/ready` via `HttpClient`, or a statically
linked busybox copy) would avoid the apt network dependency. Acceptable as-is given the
repo-wide convention.

---

_Reviewed: 2026-05-30_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
