---
phase: 70
slug: processor-inject-cleanup
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-16
---

# Phase 70 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Detailed rationale (false-positive defenses, sampling per criterion) lives in
> `70-RESEARCH.md` → "## Validation Architecture".

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 + Microsoft.Testing.Platform (MTP) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `dotnet test tests/BaseApi.Tests -- --filter-method "*Keeper*\|*KeeperContract*\|*PipelineForward*"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests` |
| **Estimated runtime** | ~30–90 s (full suite ~638 facts) |

> **MTP filter caveat:** `dotnet test --filter` is silently ignored under xunit.v3/MTP
> (it runs all facts). Use the post-`--` form `-- --filter-method <pattern>` for scoped runs.

---

## Sampling Rate

- **After every task commit:** Run the **quick run command** (scoped to the touched fact class).
- **After every plan wave:** Run the **full suite command** — 0 failures.
- **Before `/gsd-verify-work`:** Full suite green **and** `dotnet build -c Release` + `dotnet build -c Debug` both 0-warning (D-10 / KINJ-02).
- **Max feedback latency:** ~90 s.

---

## Per-Task Verification Map

> Task IDs are assigned by the planner. Each row binds a success criterion to its automated
> assertion. Populate `Task ID` / `Plan` / `Wave` once plans exist (Nyquist auditor finalizes).

| Task ID | Plan | Wave | Requirement | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|-----------------|-----------|-------------------|-------------|--------|
| 70-01-?? | 01 | 1 | KINJ-01 | `InjectConsumer` performs write+send only; never calls a `KeyDelete*` overload | unit (NSubstitute behavioral) | `dotnet test tests/BaseApi.Tests -- --filter-method "*InjectConsumerFacts*"` | ✅ existing | ⬜ pending |
| 70-01-?? | 01 | 1 | KINJ-02 | `KeeperInject` has no `DeleteEntryId`; `BuildInject` omits it; reflection `Assert.Null` guards it | unit (reflection) | `dotnet test tests/BaseApi.Tests -- --filter-method "*KeeperContractTests*"` | ✅ existing | ⬜ pending |
| 70-01-?? | 01 | 1 | KINJ-02 | `PipelineForwardFacts` + `SC2RecoveryPathsE2ETests` compile & green on the reduced shape | unit + E2E | `dotnet test tests/BaseApi.Tests -- --filter-method "*PipelineForward*\|*SC2Recovery*"` | ✅ existing | ⬜ pending |
| 70-01-?? | 01 | 1 | KINJ-03 | Dedicated invariant fact: `DeleteConsumer` deletes; `InjectConsumer` + `ReinjectConsumer` do not (both `KeyDeleteAsync` overloads) | unit (NSubstitute behavioral) | `dotnet test tests/BaseApi.Tests -- --filter-method "*DeleteInvariant*"` | ❌ W0 (new file) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] New dedicated invariant fact file (e.g. `tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs`) — the KINJ-03 cross-consumer guard. The only net-new test artifact.

*All other phase requirements are covered by existing fact classes (`InjectConsumerFacts`, `KeeperContractTests`, `PipelineForwardFacts`, `SC2RecoveryPathsE2ETests`) which are edited in place — existing infrastructure covers them.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| 0-warning build, Release **and** Debug | KINJ-02 (D-10) | Compiler-warning state is not a fact assertion | `dotnet build -c Release` then `dotnet build -c Debug`; both must report 0 warnings |

*All behavioral phase requirements have automated fact verification; only the warning-count gate is a build observation.*

---

## False-Positive Defenses (summary — see 70-RESEARCH.md for detail)

- **"build/types green while a delete still occurs":** behavioral `DidNotReceive` on **both**
  `KeyDeleteAsync` overloads, co-asserted with a **positive** side-effect (write+send received) so a
  no-op consumer cannot trivially pass the negative guard.
- **"field silently re-added":** reflection `Assert.Null(GetProperty("DeleteEntryId"))` in the contract test.
- **Carve-out:** the invariant fact never instantiates `L2ProbeRecovery`, so its scratch self-delete
  cannot trip the negative guard (this is why D-04 chose behavioral over an IL/source scan).

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers the new invariant fact file
- [ ] No watch-mode flags
- [ ] Feedback latency < 90 s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
