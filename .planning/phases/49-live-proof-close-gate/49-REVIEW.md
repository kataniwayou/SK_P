---
phase: 49-live-proof-close-gate
reviewed: 2026-06-09T00:00:00Z
depth: standard
files_reviewed: 4
files_reviewed_list:
  - tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs
  - scripts/phase-49-close.ps1
findings:
  critical: 0
  warning: 3
  info: 6
  total: 9
status: issues_found
---

# Phase 49: Code Review Report

**Reviewed:** 2026-06-09
**Depth:** standard
**Files Reviewed:** 4
**Status:** issues_found

## Summary

Reviewed 3 newly-authored RealStack E2E test files plus the Phase-49 triple-SHA close-gate
PowerShell script. No production code changed. Overall quality is high: the const-vs-literal
discipline flagged in the phase context is fully honored (no `"keeper-recovery"` /
`"skp-dlq-1"` literals appear in the new C# — both are referenced via `KeeperQueues.Recovery`
and `ConsolidatedErrorTransportFilter.Dlq1`, which I verified exist with those values), the
net-zero key/queue teardown is correct and matches the proven production SREM/SADD member
format (`workflowId.ToString("D")` against `ParentIndex()` = `"skp:"`), the SC3
`docker stop`/`start` heal-in-`finally` is correctly guarded by `redisStopped`, and the
contract ctors + L2 key builders the tests call all line up with `src/Messaging.Contracts`.

The findings below are correctness/robustness risks in the test polling and teardown harness
plus minor quality items. None are blocking for an authored-and-hermetically-green proof
posture — they are flagged for the live operator run where flakiness and teardown leaks would
surface as close-gate SHA mismatches.

No Critical issues found.

## Warnings

### WR-01: SC2 broker teardown helpers swallow non-zero docker exit, so a failed DLQ purge silently leaks depth>0

**File:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs:379-405`
**Issue:** `RunRabbitCtlAsync` (used by both `PurgeQueueAsync` and `DeleteQueueAsync`, which run
in `DisposeAsync`) never inspects `proc.ExitCode`. If the `docker exec ... rabbitmqctl purge_queue skp-dlq-1`
command fails (container name typo, rabbitmqctl transient error, queue lock), the parked
data-gone DLQ message is NOT drained, yet teardown reports success. The close gate's separate
`skp-dlq-1 depth==0` assertion (`scripts/phase-49-close.ps1:360-370`) would then fail on the
*next* run with no indication the failure originated in this test's teardown. The same applies
to the `delete_queue` of the re-inject queue feeding the rabbitmq name-SHA invariant. Note
`ReadQueueDepthAsync` and `DockerAsync` (SC3) DO surface failures (one throws on null start,
the other throws on non-zero exit) — this teardown path is the inconsistent outlier.
**Fix:** After `WaitForExitAsync`, when `proc.ExitCode != 0`, surface it loudly enough to be
diagnosable without failing the (already-green) assertion — e.g. capture stderr and write it via
`TestContext.Current` / a thrown-then-caught diagnostic, or at minimum read stderr:
```csharp
using var proc = Process.Start(psi);
if (proc is null) return;
var stderr = await proc.StandardError.ReadToEndAsync();
_ = await proc.StandardOutput.ReadToEndAsync();
await proc.WaitForExitAsync();
if (proc.ExitCode != 0)
    throw new InvalidOperationException(
        $"rabbitmqctl {string.Join(' ', ctlArgs)} exited {proc.ExitCode}: {stderr}");
```
(If the throw-in-DisposeAsync risk is unwanted, route the message to the test output sink instead
of swallowing it entirely — the current silent swallow is the problem.)

### WR-02: SC2 stderr is redirected but never drained — risk of a teardown/read hang on a chatty rabbitmqctl

**File:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs:333-365, 379-405`
**Issue:** Both `ReadQueueDepthAsync` and `RunRabbitCtlAsync` set `RedirectStandardError = true`
but only read `StandardOutput`. If `rabbitmqctl` writes enough to stderr to fill the OS pipe
buffer (warnings, deprecation notices, broker connection diagnostics), the child process blocks
on its stderr write while the test blocks on `StandardOutput.ReadToEndAsync()` / `WaitForExitAsync()`
— a classic deadlock. SC3's `DockerAsync` (`SC3...cs:298-300`) correctly drains BOTH streams; SC2
should match. This is latent (rabbitmqctl `-q` is usually quiet) but real on the live stack.
**Fix:** Drain stderr concurrently in both SC2 methods, mirroring `DockerAsync`:
```csharp
var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
var stderrTask = proc.StandardError.ReadToEndAsync(ct);
await Task.WhenAll(stdoutTask, stderrTask);
await proc.WaitForExitAsync(ct);
var stdout = await stdoutTask;
```

### WR-03: SC2 REINJECT data-present assertion can pass on a pre-existing queue (no BEFORE baseline) — false green if the procId queue already holds messages

**File:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs:108-113`
**Issue:** State 1 asserts `depth >= 1` on `queue:{procId:D}` with NO before-baseline read, unlike
State 2 which correctly reads `dlqBefore` and asserts `>= dlqBefore + 1`. The comment claims the
queue is fresh ("no consumer is bound to this fresh procId queue"), and `procId = Guid.NewGuid()`
makes a collision astronomically unlikely — so this is low-probability. But the asymmetry means
the assertion does not actually prove *the re-inject* landed a message; it proves the queue has
≥1 message from any source. On a re-run where teardown's `delete_queue` failed (see WR-01), a
stale message could satisfy `depth >= 1` even if the consumer never re-injected. Make it a
delta-assert for parity with State 2 and to harden the proof.
**Fix:** Read a baseline before `endpoint.Send` and assert the increment:
```csharp
var originQueue = procId.ToString("D");
var before = await ReadQueueDepthAsync(originQueue, ct);          // 0 on a truly fresh queue
await endpoint.Send(new KeeperReinject(...) { ... }, ct);
var depth = await PollForQueueDepthAsync(originQueue, minDepth: before + 1, ct);
Assert.True(depth >= before + 1, $"...expected re-inject to climb queue:{originQueue} past {before}...");
```

## Info

### IN-01: SC2 `ScanExecutionDataKeys` lacks the RedisException tolerance SC3 added — and SC1 likewise

**File:** `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs:280-300`; `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs:266-286`
**Issue:** SC3's `ScanExecutionDataKeys` wraps the connect/scan in `try { } catch (RedisException)`
(`SC3...cs:412-440`) because its docker-stop window makes redis unreachable. SC1/SC2 do not stop
redis, so the bare version is functionally correct. This is noted only as a divergence: if any of
these helpers is ever copied into a context that touches a redis outage, the SC1/SC2 shape would
throw rather than return a partial set. Not a bug today.
**Fix:** Optional — none required; consider hoisting the SC3 tolerant variant into a shared test
helper to remove the three near-duplicate copies (see IN-02).

### IN-02: Three near-identical `RealStackWebAppFactory` + scan/poll/seed clones across SC1/SC2/SC3

**File:** `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs:362-440`; `SC2RecoveryPathsE2ETests.cs:419-513`; `SC3PauseResumeOutageE2ETests.cs:510-588`
**Issue:** The nested `RealStackWebAppFactory`, the env-var host overrides, `PollForHealthyLivenessAsync`,
`PollForNewExecutionDataKeyAsync`, `ScanExecutionDataKeys`, and the three `Seed*Async` helpers are
copy-pasted (the XML docs say so explicitly: "CLONED WHOLESALE"). This is a deliberate, documented
proof-phase choice and the per-file divergences (SC2's broker queues, SC3's redis-tolerant scan)
are real, so consolidation is non-trivial. Flagged as the standing duplication-maintenance cost: a
fix to the shared liveness/scan logic must be applied in 2-3 places.
**Fix:** Optional post-proof refactor — extract a shared `RealStackWebAppFactory` base + a
`RealStackPolls` static helper into the test project; let each SCx subclass only the queue-teardown
extension it needs. Out of scope for the proof phase.

### IN-03: `delay` exponential-backoff variable in the L2 poll loops never reaches its computed value before being capped

**File:** `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs:237-251`; `SC2...cs:232-247`; `SC3...cs:355-369`
**Issue:** Pattern `await Task.Delay(Math.Min(delay, 3_000)); delay = Math.Min(delay * 2, 3_000);`
starts at 1000 and reaches the 3000 cap after one doubling, so the backoff is effectively
1s, 2s, 3s, 3s… The `delay = Math.Min(delay*2, 3_000)` and the `Math.Min(delay, 3_000)` at the call
site are redundant (the stored `delay` is already capped, so the call-site `Math.Min` is a no-op
after the first cap). Harmless, just slightly confusing dead arithmetic.
**Fix:** Drop the call-site `Math.Min` since `delay` is already capped on assignment:
`await Task.Delay(delay, ct); delay = Math.Min(delay * 2, 3_000);`

### IN-04: SC1 net-zero "stop the workflow" runs AFTER all assertions — an early assertion failure leaves the cron firing and churns the close-gate scan

**File:** `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs:184-186`
**Issue:** The best-effort `orchestration/stop` is the last statement in the test body, not in
`DisposeAsync`. If any assertion between Start and that line throws (e.g. the ES advance poll times
out), the self-rescheduling `* * * * *` workflow keeps firing and minting fresh `skp:data:*` keys,
which the comment itself warns "churns the close-gate redis --scan name-set." The L2KeysToCleanup
drain in DisposeAsync deletes the *known* root/step keys but not the unbounded future per-fire keys
a still-running cron mints. SC3 has the same structure (`SC3...cs:236-237`). The close-gate settle
loop (`phase-49-close.ps1:280-293`) relies on every E2E "STOPS its workflow in teardown" — an
assertion-failure path violates that precondition.
**Fix:** Move the workflow stop into the factory's `DisposeAsync` (register `wfId` into a
`WorkflowsToStop` list the same way keys are registered), so it runs even when an assertion throws:
```csharp
factory.WorkflowsToStop.Add(wfId);   // stopped in DisposeAsync via the in-process client, best-effort
```

### IN-05: `phase-49-close.ps1` DLQ depth regex assumes a single-token queue name and would silently mis-read a name containing whitespace

**File:** `scripts/phase-49-close.ps1:362-363`
**Issue:** `^\s*$([regex]::Escape($q))\s+\d+\s*$` matches a `name<whitespace>messages` row. For the
fixed literal `skp-dlq-1` this is correct. It is noted only because the depth parse falls back to
`-1` (treated as a violation) if the row format ever shifts (e.g. rabbitmqctl emitting tabs vs
spaces, or an added column) — a brittle-but-fail-safe parse. The fallback direction is correct
(unparseable → `-1` → fails the gate, not a false pass), so this is informational.
**Fix:** None required; the fail-closed fallback is the right call. Optionally split on tabs like the
C# `ReadQueueDepthAsync` does for consistency.

### IN-06: `phase-49-close.ps1` seeds Processor `version='3.5.0'` from a hard-coded comment-cited appsettings value — a drift risk

**File:** `scripts/phase-49-close.ps1:132-142`
**Issue:** The create-branch hardcodes `version = '3.5.0'` with a comment citing
`src/Processor.Sample/appsettings.json:11`. The Version field does NOT participate in the unique
`uq_processor_source_hash` constraint (the SourceHash does), so a stale version here does not break
the idempotent GET-or-create or the procId stability — but it is a documentation/accuracy trap: if
`Processor.Sample`'s version bumps, this seed silently records a wrong version on a fresh-DB first
run. Low impact (only the first-ever seed on an empty DB, and version is cosmetic for this gate).
**Fix:** Optional — read the version from the same assembly the hash is read from, or drop the
version assertion's significance with a comment that it is cosmetic-only for the gate.

---

_Reviewed: 2026-06-09_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
