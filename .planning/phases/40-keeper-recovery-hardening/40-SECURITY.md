---
phase: 40-keeper-recovery-hardening
security_audit: true
asvs_level: default
audited: 2026-06-07
auditor: gsd-security-auditor (claude-sonnet-4-6)
threats_total: 10
threats_closed: 10
threats_open: 0
---

# Phase 40 Security Audit

## Threat Verification Summary

**Threats Closed:** 10/10
**Threats Open:** 0/10

Initial audit found 8/10 closed with T-40-05 and T-40-06 OPEN (residual gaps confirmed against the
code review's WR-01 / WR-03). Both were fixed in commit `9e9b578` and independently re-verified CLOSED
with hermetic-test evidence. All threats now have verified mitigations or documented accepted-risk
dispositions.

---

## Per-Threat Verification Table

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-40-01 | Tampering/Correctness | mitigate | CLOSED | `KeeperRecoveryHandler.cs` is the single body; both consumers are one-line delegators (`FaultEntryStepDispatchConsumer.cs:20-22`, `FaultExecutionResultConsumer.cs:23-25`); exactly 3 data deltas substituted; hermetic suite GREEN |
| T-40-02 | Info disclosure | accept | CLOSED | `KeeperRecoveryHandler.cs:68-71` — intake log uses structured params `{FaultType}`, `{H}`, `{ExceptionType}`, `{ExceptionMessage}`; no stack frames, no interpolation; T-35-04/05 disposition carries verbatim |
| T-40-03 | Repudiation/Correctness | mitigate | CLOSED | `FaultEntryStepDispatchConsumerDefinition.cs` / `FaultExecutionResultConsumerDefinition.cs` byte-unchanged (last git touch is Phase 39); retry middleware owned by Dispatch definition only; Result definition `ConfigureConsumer` is an empty no-op |
| T-40-04 | DoS (self-DoS) | mitigate | CLOSED | `KeeperRecoveryHandler.cs` cap check at top of Recovered branch; atomic INCR + cap gate + park + return; proven hermetically by `KeeperRecoverCapTests.Cap_Honored_ExactlyCapReinjectsThenOnePark` |
| T-40-05 | DoS (Redis growth) | mitigate | CLOSED | **Fixed in `9e9b578`.** Atomic Lua `IncrWithTtl` (`KeeperRecoveryHandler.cs:46-49,112-115`): `INCR` + `PEXPIRE` (only on `n==1`) in one `ScriptEvaluateAsync` round-trip — the key cannot exist without its TTL, eliminating the crash window; no-clobber preserved. `CounterTtlMillis = 300_000` in one place. `FakeRedis.cs:177-191` mirrors the semantics; proven by `Wr01_CounterKey_BornWithTtl_AtomicallyAndNotClobberedOnReincrement` (`KeeperRecoverCapTests.cs:155`) |
| T-40-06 | Tampering (double-park race) / silent drop | mitigate | CLOSED | **Fixed in `9e9b578`.** Park gate relaxed to `n >= cap+1` (`KeeperRecoveryHandler.cs:117`); a failed cap-park `Send` now propagates before the DEL so the `Immediate(N)` retry re-parks (no silent drop); DEL runs only after a successful park; metrics recorded only after a durable Send. Proven by `Wr03_CapPark_FailedSend_IsRetried_ThenParksExactlyOnce` (`KeeperRecoverCapTests.cs:198`). See accepted residual below. |
| T-40-07 | Availability (live flood) | mitigate | CLOSED | `KeeperRecoverCapTests.cs` — no `[Trait("Category","RealStack")]` anywhere; both (now four) facts are hermetic (in-memory harness + FakeRedis); a live cap test remains FORBIDDEN |
| T-40-08 | Correctness (false-pass) | mitigate | CLOSED | `KeeperRecoveryE2ETests.cs:554-578` — `DrainKeeperDlqUntilStablyEmptyAsync`: 2s poll, 15s stability window, 90s cap, `Assert.Fail` on timeout, re-purge each iteration, `int.MaxValue` fallback on read failure |
| T-40-09 | Correctness (gate drift) | mitigate | CLOSED | `scripts/phase-39-close.ps1` byte-unchanged (last touch Phase 39); no `DrainKeeperDlq`/keeper-dlq purge in the script — gate stays snapshot-only |
| T-40-10 | Info disclosure | accept | CLOSED | `KeeperRecoveryE2ETests.cs:514-521,531-534` — guest:guest Basic auth at `localhost:15673`, identical to pre-existing `PurgeKeeperDlqAsync`; no new credential surface |

---

## Accepted Risks Log

| ID | Category | Rationale |
|----|----------|-----------|
| T-40-02 | Info disclosure | Intake log surfaces only `ExceptionType` + `Message` as structured params; no stack frames, no interpolation. Carries the T-35-04/05 disposition verbatim. |
| T-40-10 | Info disclosure | guest:guest Basic auth is a test-only local dev-stack credential, unchanged from the pre-existing `PurgeKeeperDlqAsync`. No new exposure. |
| T-40-06 (residual) | Duplicate park under concurrent race | The WR-03 fix relaxes the strict single-winner gate (`n == cap+1`) to retry-safe must-park (`n >= cap+1`). This eliminates the silent-drop bug, but under a *true* concurrent 2-replica race two replicas may each park (one at `n==cap+1`, one at `n==cap+2` if the first's DEL races the second's INCR), producing a DUPLICATE park to `keeper-dlq`. **Accepted:** a duplicate park is absorbed by T-40-08's poll-until-stably-empty `keeper-dlq` drain (and is documented in the handler comment at `KeeperRecoveryHandler.cs:123-125`). Trading at-most-one-park for never-silently-drop is the correct bias for a recovery-hardening path. |

---

## Audit Trail

### Security Audit 2026-06-07 (initial)
| Metric | Count |
|--------|-------|
| Threats found | 10 |
| Closed | 8 |
| Open | 2 (T-40-05, T-40-06) |

T-40-05: non-atomic `INCR`/`EXPIRE` crash window → un-TTL'd counter-key leak (= WR-01).
T-40-06: failed cap-park `Send` + retry → `n=cap+2` non-parking return → silent `Fault<T>` drop (= WR-03).

### Fix 2026-06-07 — commit `9e9b578`
`fix(40): atomic INCR+PEXPIRE counter TTL + retry-safe cap-park (WR-01/T-40-05, WR-03/T-40-06)`
- WR-01: replaced the two-round-trip INCR+EXPIRE with a single atomic Lua `ScriptEvaluateAsync` (`INCR`; `PEXPIRE` only on `n==1`).
- WR-03: relaxed the park gate to `n >= cap+1`; failed Send propagates before DEL so the retry re-parks; DEL only after a successful park.
- Test infra: `FakeRedis` now tracks per-key TTL and supports `ScriptEvaluateAsync`; two new hermetic facts. Full hermetic suite 495 passed / 0 failed / 0 skipped; Release 0-warning.

### Security Audit 2026-06-07 (re-verification)
| Metric | Count |
|--------|-------|
| Threats found | 10 |
| Closed | 10 |
| Open | 0 |

T-40-05 and T-40-06 independently re-verified CLOSED against the fixed code + new tests. **threats_open: 0.**
