---
phase: 28-sourcehash-identity-processor-sample-e2e-closeout
reviewed: 2026-06-02T00:00:00Z
depth: standard
files_reviewed: 18
files_reviewed_list:
  - SK_P.sln
  - compose.yaml
  - scripts/phase-28-close.ps1
  - scripts/verify-sourcehash-reproducible.ps1
  - src/BaseProcessor.Core/SourceHash.targets
  - src/Processor.Sample/Dockerfile
  - src/Processor.Sample/Processor.Sample.csproj
  - src/Processor.Sample/Program.cs
  - src/Processor.Sample/SampleProcessor.cs
  - src/Processor.Sample/appsettings.json
  - tests/BaseApi.Tests/BaseApi.Tests.csproj
  - tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs
  - tests/BaseApi.Tests/Endpoints/TestController.cs
  - tests/BaseApi.Tests/Middleware/ConcurrencyTokenTests.cs
  - tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs
  - tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs
  - tests/BaseApi.Tests/Processor/SourceHashEmbedFacts.cs
findings:
  critical: 0
  warning: 2
  info: 5
  total: 7
status: issues_found
---

# Phase 28: Code Review Report

**Reviewed:** 2026-06-02T00:00:00Z
**Depth:** standard
**Files Reviewed:** 18
**Status:** issues_found

## Summary

Phase 28 lands the SourceHash build-time identity mechanism (`SourceHash.targets`),
the first concrete `Processor.Sample` console + Dockerfile, the steady-state
`processor-sample` compose service, two real-stack capstone E2E tests, hermetic
SourceHash/SampleProcessor facts, and the triple-SHA close gate. Overall quality
is high: the design intent is densely documented, the cross-OS reproducibility
concern (Pitfall 1) is explicitly engineered in the MSBuild task (forward-slash
normalization + ordinal sort + LF content normalization), and the E2E teardown
discipline (net-zero Redis/RMQ snapshots) is carefully maintained.

No Critical issues found. No security vulnerabilities in scope — the only credentials
present (`guest`/`guest`, `postgres`/`postgres`) are documented dev-only postures
sourced from compose env, consistent with the established codebase convention and
prior-phase decisions, not new hardcoded production secrets.

Two Warnings concern correctness/robustness in the close gate and the verify script
(both PowerShell tooling, not shipped runtime code). Info items are minor
maintainability notes.

## Warnings

### WR-01: Close gate reflects the host-built Sample DLL but never asserts host==Docker hash equality

**File:** `scripts/phase-28-close.ps1:57-78`
**Issue:** The gate seeds the steady-state Processor row using the SourceHash
reflected off the **host-built** `Processor.Sample.dll` (`bin/Release` or
`bin/Debug`). The live `processor-sample` container, however, runs the
**Linux-Docker-built** assembly. The whole identity loop only resolves if those two
hashes are byte-identical (the exact concern `verify-sourcehash-reproducible.ps1`
exists to prove). The close gate does not invoke that verification, nor does it
otherwise guard the equality — it relies entirely on the operator having run the
verify script separately. If the host and Docker hashes diverge, the gate seeds a row
the container can never match, `processor-sample` never goes Healthy, and the failure
surfaces 120s later as the generic "never reported healthy" abort (line 127-131) with
no hint that a hash divergence (not a container/infra problem) is the root cause. The
abort message points the operator at `docker compose logs` rather than at the
divergence the verify script would pinpoint.
**Fix:** Either invoke the reproducibility gate as a pre-flight step inside the close
gate, or extend the post-seed timeout abort to call it out explicitly. Minimal version:
```powershell
# After reading $sourceHash (host build) and before/at the health-wait abort, surface the likely cause:
if (-not $procHealthy) {
    Write-Host "processor-sample never reported healthy within 120s after seeding the row. Aborting." -ForegroundColor Red
    Write-Host "  LIKELY CAUSE: host-built SourceHash ($sourceHash) may diverge from the Linux-Docker hash" -ForegroundColor Red
    Write-Host "  the container runs. Run: pwsh -File scripts/verify-sourcehash-reproducible.ps1" -ForegroundColor Red
    Write-Host "  Otherwise check 'docker compose logs processor-sample'." -ForegroundColor Red
    exit 2
}
```

### WR-02: `Read-SerString` does not handle the compressed-length sentinel bytes and trusts blob layout without bounds checks

**File:** `scripts/verify-sourcehash-reproducible.ps1:45-60`
**Issue:** The hand-rolled ECMA-335 SerString decoder reads the compressed length
prefix but (a) does not validate that `$p`/`$p+len` stay within `$Bytes.Length`, and
(b) the 2-byte and 4-byte branches assume well-formed input. For the specific blob this
script decodes (`AssemblyMetadata("SourceHash", "<64-hex>")`) the layout is fixed and
safe, so this will not misfire in practice. But if the attribute blob layout ever
changes (e.g. a future attribute reordering, or a malformed/truncated PE), the decoder
indexes past the array and throws an opaque `IndexOutOfRangeException` instead of the
intended clear `"No SourceHash attribute found"` error — making a real divergence/build
problem present as a confusing crash. This is read-only diagnostic tooling, so impact is
bounded to developer experience.
**Fix:** Add a bounds guard at the top of `Read-SerString` and let the caller's
`throw "No [assembly: AssemblyMetadata('SourceHash', ...)]"` remain the single clear
failure surface:
```powershell
function Read-SerString {
    param([byte[]]$Bytes, [ref]$Pos)
    $p = $Pos.Value
    if ($p -ge $Bytes.Length) { throw "SerString read past end of blob (corrupt or unexpected attribute layout)." }
    if ($Bytes[$p] -eq 0xFF) { $Pos.Value = $p + 1; return $null }
    # ... existing length decode ...
    if (($p + $len) -gt $Bytes.Length) { throw "SerString length $len exceeds blob bounds." }
    # ... existing UTF8 decode ...
}
```

## Info

### IN-01: `Service.Version` / Processor row version is a hardcoded string duplicated across three places

**File:** `src/Processor.Sample/appsettings.json:11`, `scripts/phase-28-close.ps1:100`
**Issue:** The version `3.5.0` is repeated in `appsettings.json` (`Service.Version`)
and in the close gate's POST body (`version = '3.5.0'`). The SourceHash row's `version`
field is metadata only (it does not participate in identity — identity is the hash), so
drift between these is harmless to the gate's idempotency. Still, two literals that are
"meant" to agree will silently diverge on the next version bump.
**Fix:** Acceptable as-is given version is non-identity metadata. If desired, have the
gate read `Service.Version` from `appsettings.json` rather than hardcoding `'3.5.0'`.

### IN-02: `Assembly.Load([byte[]])` comment claims a "throwaway context" but loads into the default ALC

**File:** `scripts/phase-28-close.ps1:69-71`
**Issue:** The comment says the bytes are loaded "into a throwaway context so the file
handle is released afterward." `[System.Reflection.Assembly]::Load($asmBytes)` reads
from a byte array (no file handle is held in the first place — that goal is already met
by `ReadAllBytes`), but it loads into the **default** AssemblyLoadContext and cannot be
unloaded for the process lifetime. The comment overstates the isolation. Functionally
fine since the script is short-lived and exits, but the rationale is inaccurate, and it
diverges from the verify script's cleaner reflection-metadata approach
(`PEReader` + `GetMetadataReader`) that loads nothing into the ALC.
**Fix:** Either correct the comment, or reuse the verify script's `Get-EmbeddedSourceHash`
metadata-reader approach for consistency (no assembly load, no ALC residue).

### IN-03: `PollForNewExecutionDataKeyAsync` returns the FIRST unknown `skp:data:*` key, which can be a concurrent run's

**File:** `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs:215-240`
**Issue:** The poll returns the first `skp:data:*` key not present in the pre-Start
snapshot. If another caller (a parallel test or a second orchestrator fire) writes an
execution-data key into the same host Redis during the poll window, this test could pick
up a foreign key and then register it for teardown — deleting another run's data. The
`[Collection("Observability")]` serialization plus the RealStack filter make concurrent
writers unlikely in the intended single-run close-gate context, so this is a latent
robustness note rather than an observed bug.
**Fix:** Acceptable given the serialized single-run usage. If hardening is wanted, scope
the freshness window tighter (capture a timestamp and only accept keys whose backing
data was written after Start) or assert exactly one new key appeared.

### IN-04: `ScanExecutionDataKeys` opens a fresh synchronous multiplexer on every poll iteration

**File:** `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs:247-267`
**Issue:** Each poll iteration calls `ScanExecutionDataKeys()`, which does
`ConnectionMultiplexer.Connect(HostRedis)` (synchronous connect) inside a `using` and
tears it down. Over a 120s poll budget with backoff this opens/closes many short-lived
connections. Correctness is fine (the `using` disposes each), and this is test-only code,
so it is out of the v1 performance scope — noted only as a minor pattern smell since the
rest of the file uses `ConnectAsync` with a single reused multiplexer
(`PollForHealthyLivenessAsync`).
**Fix:** Optional — hoist a single `ConnectionMultiplexer` for the test and pass its
`IDatabase`/server handles into the scan helper.

### IN-05: `start_period: 30s` may be tight for the processor-sample boot-before-register identity loop

**File:** `compose.yaml:252`
**Issue:** `processor-sample` uses `start_period: 30s` / `retries: 5` (mirroring the
orchestrator). But unlike the orchestrator, processor-sample goes Healthy only AFTER it
resolves identity over the bus against a seeded Processor DB row (boot-before-register,
unbounded retry). The close gate handles this by seeding the row first and then waiting
up to 120s for health — so the gate is correct. However, for a plain `docker compose up`
(no pre-seed), the container will legitimately stay unhealthy until a row exists, and the
30s/5-retry window is purely cosmetic in that scenario. This is by-design per the phase
notes, not a bug — flagged only so a future reader does not "fix" the unhealthy state by
inflating start_period.
**Fix:** None required. The behavior is intentional and documented in the compose comment
block (lines 216-226) and the close-gate header.

---

_Reviewed: 2026-06-02T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
