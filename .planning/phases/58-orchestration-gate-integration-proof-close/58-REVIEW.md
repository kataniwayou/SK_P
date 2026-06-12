---
phase: 58-orchestration-gate-integration-proof-close
reviewed: 2026-06-13T00:00:00Z
depth: standard
files_reviewed: 16
files_reviewed_list:
  - SK_P.sln
  - compose.yaml
  - scripts/phase-58-close.ps1
  - src/Processor.BadConfig/BadConfig.cs
  - src/Processor.BadConfig/BadConfigProcessor.cs
  - src/Processor.BadConfig/Dockerfile
  - src/Processor.BadConfig/Processor.BadConfig.csproj
  - src/Processor.BadConfig/Program.cs
  - src/Processor.BadConfig/appsettings.json
  - tests/BaseApi.Tests/BaseApi.Tests.csproj
  - tests/BaseApi.Tests/Orchestrator/GateACompositionE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs
findings:
  critical: 0
  warning: 1
  info: 6
  total: 7
status: issues_found
---

# Phase 58: Code Review Report

**Reviewed:** 2026-06-13
**Depth:** standard
**Files Reviewed:** 16
**Status:** issues_found

## Summary

Phase 58 adds the Gate-A INCOMPATIBLE subject (`Processor.BadConfig`), a profile-gated compose
service, a triple-SHA close gate, and a set of RealStack E2E proofs. The code is well-structured,
heavily documented, and the intentional config clash (`BadConfig(int Quantity)` vs a schema typing
`quantity` as `string`) is by-design and correctly excluded from scope per the review brief.

No critical or security issues were found. The `RabbitMq:Password=guest` / `guest` credentials in
`appsettings.json` and `compose.yaml` are local-dev broker defaults consistent with every other
processor in the repo (Processor.Sample uses the same), not a Phase-58-introduced secret leak — not
flagged.

The findings below are all maintainability / dead-code observations. The one Warning is a latent
net-zero-teardown gap in `SC2RecoveryPathsE2ETests` that the close gate currently masks because the
data-gone path is a by-design silent drop — worth recording so a future contract change does not
silently regress the close-gate `skp-dlq-1` depth==0 invariant.

## Warnings

### WR-01: SC2 declares a DLQ-purge teardown channel that is never populated — a future data-gone-throws regression would leak to the close gate undetected

**File:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs:678,700-703` (and `566-569`)
**Issue:**
`BrokerQueuesToPurge` (declared :678, drained in `DisposeAsync` :700-703 via `PurgeQueueAsync` :566)
is never `.Add()`ed anywhere in the test body. The STATE-2 "REINJECT data-gone" case is documented as
a by-design silent drop (no DLQ increment), so today nothing lands in `skp-dlq-1` and there is nothing
to purge — the channel is correctly inert *given current behavior*. The risk is that this teardown
hook exists precisely to keep the close gate's `skp-dlq-1` depth==0 invariant (`scripts/phase-58-close.ps1:436-445`)
green if the data-gone path ever DID dead-letter. Because it is wired but never fed, if a future
change makes data-gone throw → dead-letter, the message would park in `skp-dlq-1`, the test's own
positive assertions could still pass, and the leak would surface only as a close-gate failure (~50min
later) with no test-local signal pointing back here.
**Fix:** Either (a) defensively register the DLQ in STATE 2 so teardown is self-healing regardless of
future behavior:
```csharp
// STATE 2, after the silent-drop assertions:
factory.BrokerQueuesToPurge.Add(ConsolidatedErrorTransportFilter.Dlq1);
```
or (b) if the data-gone-is-a-drop contract is considered frozen, delete the unused `BrokerQueuesToPurge`
property + `PurgeQueueAsync` helper (see IN-01) to remove the misleading "this is handled" signal.
Option (a) is safer for a close-gate fixture.

## Info

### IN-01: Dead helper methods `PurgeQueueAsync` / `DeleteQueueAsync` reachable only via never-populated lists

**File:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs:565-575`
**Issue:** `PurgeQueueAsync` (:566) is only called from the `BrokerQueuesToPurge` drain, which is never
populated (see WR-01). `DeleteQueueAsync` (:572) IS exercised (STATE 1 adds to `BrokerQueuesToDelete`
:135), so only the purge path is dead. If WR-01 is resolved via option (a), `PurgeQueueAsync` becomes
live and this is moot.
**Fix:** Resolve under WR-01 — either feed `BrokerQueuesToPurge` or remove `PurgeQueueAsync` +
`BrokerQueuesToPurge` together.

### IN-02: `BadConfigProcessor.ProcessAsync` mints a `Guid.NewGuid()` on a documented dead path

**File:** `src/Processor.BadConfig/BadConfigProcessor.cs:25-30`
**Issue:** The override is correctly documented as never-invoked (Gate A withholds the queue bind). It
logs at `Information` and returns a single `Completed` item with `Guid.NewGuid()`. This is harmless and
satisfies the abstract contract, but the `LogInformation` text ("unexpected — Gate A should have
withheld health") describes an invariant violation — if it ever fires it is an error condition, not an
info event.
**Fix:** Promote the log to `LogWarning` (or `LogError`) so that an unexpected invocation is visible at
default log levels and surfaces in the ES proofs rather than being filtered as routine info:
```csharp
logger.LogWarning("badconfig transform invoked (unexpected — Gate A should have withheld health)");
```

### IN-03: Close-gate DLQ-depth parse double-evaluates the regex and silently coerces a no-match to a passing-adjacent value

**File:** `scripts/phase-58-close.ps1:437-438`
**Issue:** `$line` is assigned from a `Where-Object` (may yield an array), then line 438 re-runs
`-match "\s+(\d+)\s*$"` against it to extract the depth, defaulting to `-1` on no-match. The `-1`
sentinel is correctly treated as a violation (`$depth -ne 0`), so this is safe today. But the two
separate regexes (`^\s*name\s+\d+\s*$` then `\s+(\d+)\s*$`) parsing the same TAB-separated row is
fragile: if `rabbitmqctl` output spacing changes, the first filter could match while the second
captures the wrong column, or vice versa.
**Fix:** Parse once by splitting on whitespace/tab and indexing the message column, mirroring the more
robust `ReadQueueDepthAsync` approach already used in the SC2 test (split on `\t`, take `cols[1]`).

### IN-04: `RabbitMq__Port` host override is set in tests but absent from `Processor.BadConfig/appsettings.json`

**File:** `src/Processor.BadConfig/appsettings.json:20-24` / `compose.yaml` (processor-badconfig env)
**Issue:** The compose env for `processor-badconfig` sets `RabbitMq__Host: rabbitmq` with no `Port`
(relies on the 5672 default inside the compose network), consistent with `appsettings.json`. This is
correct for the container, but note the asymmetry with the in-process test factories which explicitly
set `RabbitMq__Port: 5673` for the host-mapped port. No action needed — recording for traceability
that the badconfig container intentionally has no port override (in-network default).
**Fix:** None required; informational. If a future deployment maps RabbitMQ to a non-default in-network
port, add `RabbitMq__Port` to the compose env.

### IN-05: Hardcoded container names couple the RealStack tests + close gate to compose `container_name`

**File:** `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs:292`,
`SC2RecoveryPathsE2ETests.cs:541,587-589`, `scripts/phase-58-close.ps1:303,309` etc.
**Issue:** `"sk-redis"` / `"sk-rabbitmq"` are string literals duplicated across the SC2/SC3 tests and
the close script (the SC3 comment even pins `compose.yaml:137`). A `container_name` rename in
`compose.yaml` would break these at runtime with no compile-time signal. This is the established pattern
across the repo's RealStack harnesses, so it is consistent — but it is a latent coupling.
**Fix:** None required for this phase. If consolidating later, hoist the container names to a single
shared test constant / a documented compose contract so a rename has one edit site.

### IN-06: `SampleRoundTripE2ETests` net-zero comment references a retired composite-key model

**File:** `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs:490-501`
**Issue:** The `DisposeAsync` GAP-49-8 sweep still scan-deletes `skp:*:{wfId}:*` composite backup keys
and its comment describes the "2-day crash-backstop" / "2 keeper replicas" Model-B composite. Per the
close-script header (`phase-58-close.ps1:367-371`), Model-B (`corr:wf:proc:exec`) was *retired in
Phases 50/53*. The sweep is now defensive dead-cleanup against a key family the system no longer mints.
It is harmless (a no-op `server.Keys` scan), but the sibling SC1/SC2/SC3 factories — cloned from this
one — correctly dropped it, leaving this capstone factory inconsistent with its own clones.
**Fix:** Remove the GAP-49-8 composite sweep (lines ~490-501) to match the SC1/SC2/SC3 factories and
the retired-model reality, or update the comment to state it is a deliberate belt-and-suspenders sweep
against a retired family. Low priority — the phase passed net-zero with it present.

---

_Reviewed: 2026-06-13_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
