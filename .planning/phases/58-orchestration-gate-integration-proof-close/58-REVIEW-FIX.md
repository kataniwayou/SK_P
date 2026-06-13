---
phase: 58-orchestration-gate-integration-proof-close
fixed_at: 2026-06-13T00:00:00Z
review_path: .planning/phases/58-orchestration-gate-integration-proof-close/58-REVIEW.md
iteration: 2
findings_in_scope: 7
fixed: 3
skipped: 4
status: partial
---

# Phase 58: Code Review Fix Report

**Fixed at:** 2026-06-13
**Source review:** .planning/phases/58-orchestration-gate-integration-proof-close/58-REVIEW.md
**Iteration:** 2

This is the SECOND fix pass (`fix_scope: all` — Info findings in scope). WR-01 was resolved in
the prior pass (commit 38d5128) and is treated as already-resolved here. The four no-action
dispositions below are user-approved (by-design / moot / acknowledged), not unresolved gaps.

**Summary:**
- Findings in scope: 7
- Fixed: 3 (IN-02, IN-03, IN-06)
- Already resolved / no-action: 4 (WR-01 prior pass, IN-01 moot, IN-04 by-design, IN-05 by-design)

## Fixed Issues

### IN-02: `BadConfigProcessor.ProcessAsync` mints a `Guid.NewGuid()` on a documented dead path

**Files modified:** `src/Processor.BadConfig/BadConfigProcessor.cs`
**Commit:** 6c9a326
**Applied fix:** Promoted the dead-path invocation log from `LogInformation` to `LogWarning` so an
unexpected Gate-A-withheld invocation surfaces at default log levels / in ES proofs instead of being
filtered as routine info. Message wording unchanged; the returned single `Completed` `ProcessItem`
shape and all other behavior are untouched. Verified: `dotnet build Processor.BadConfig.csproj -c Release`
→ 0 warnings, 0 errors.

**Note (expected SourceHash shift):** This `.cs` change is folded into Processor.BadConfig's embedded
SourceHash and WILL shift the SourceHash and the seeded procId. This is expected and user-approved —
the orchestrator will rebuild and re-run the ~50-min close gate to re-prove and update the recorded
badId.

### IN-03: Close-gate DLQ-depth parse double-evaluates the regex and silently coerces a no-match

**Files modified:** `scripts/phase-58-close.ps1`
**Commit:** b3781ae
**Applied fix:** Replaced the two-regex double-evaluation (a `Where-Object` name filter followed by a
second `\s+(\d+)\s*$` capture on the same row) with a single robust parse: split each row on
tab/whitespace, match the queue name in `cols[0]`, and `[int]::TryParse` the message-count column
`cols[1]` — mirroring the proven `ReadQueueDepthAsync` (split on `\t`, take `cols[1]`) in
SC2RecoveryPathsE2ETests.cs. Sentinel semantics preserved: `$depth` initializes to `-1` and stays
`-1` on no-match / parse-failure, so the existing `$depth -ne 0` invariant still treats failure as a
violation; valid input yields the integer depth. No other part of the script changed. Verified: AST
parse check → `PARSE OK`.

### IN-06: `SampleRoundTripE2ETests` net-zero comment references a retired composite-key model

**Files modified:** `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs`
**Commit:** 018fd7c
**Applied fix:** Removed the dead GAP-49-8 composite-key sweep (the `skp:*:{wfId}:*` `server.Keys`
scan-delete for the Model-B `corr:wf:proc:exec` composite retired in Phases 50/53) from the capstone
factory's `DisposeAsync`, aligning it with its SC1/SC2/SC3 clones which already dropped it. Behavior-
preserving no-op removal — the key family is no longer minted. Confirmed no other reference to the
removed code (`cleanupMux` remains used for the L2 cleanup; the unrelated `server.Keys` at lines
292-302 uses a separate `mux` and is untouched). Verified: `dotnet build BaseApi.Tests.csproj -c Release`
→ 0 warnings, 0 errors.

## Already Resolved / No-Action Findings

### WR-01: SC2 DLQ-purge teardown channel never populated

**Status:** Already resolved (prior fix pass, commit 38d5128). `BrokerQueuesToPurge` is now populated
in STATE 2, making the teardown self-healing. No change this pass.

### IN-01: Dead helper methods `PurgeQueueAsync` / `DeleteQueueAsync`

**Status:** Moot / resolved. `PurgeQueueAsync` is now reachable via the populated `BrokerQueuesToPurge`
(WR-01 option (a) applied in the prior pass). No code change required.

### IN-04: `RabbitMq__Port` host override absent from `Processor.BadConfig/appsettings.json`

**Status:** By design, acknowledged. The `processor-badconfig` container intentionally has no
`RabbitMq__Port` override — it relies on the in-network 5672 default, consistent with `appsettings.json`.
The asymmetry with host-mapped test factories (port 5673) is expected. No action.

### IN-05: Hardcoded container names couple RealStack tests + close gate to compose `container_name`

**Status:** By design, acknowledged. `"sk-redis"` / `"sk-rabbitmq"` string literals follow the
established repo-wide RealStack harness pattern and are consistent across SC2/SC3/close-gate. Latent
coupling noted for a future consolidation; no action this phase.

---

_Fixed: 2026-06-13_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 2_
