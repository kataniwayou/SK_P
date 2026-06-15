---
phase: 69
slug: align-processor-pipeline-to-canonical-recovery-spec-atomic-i
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-16
---

# Phase 69 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| processor → L2 (Redis) | The forward-Post index+data write crosses into shared L2 state observable by a concurrent Recovery pass / redelivery. | Slot-array index entry (messageId hash) + execution-data blob (`item.Data`), per-item; non-secret operational payload. |
| processor → L2 (index key) | The forward cleanup tail deletes the index `L2[messageId]`; the keeper (INJECT) also touches L2 for the same escalated item — a race window on the index key. | Index key existence / deletion; control-plane state. |
| processor → keeper (recovery queue) | An infra-failed atomic write escalates out-of-band to the keeper via `SendKeeper(BuildInject(...))`. | `KeeperInject` recovery message (entryId, data, ids); operational payload. |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-69-01 | Tampering / Information Disclosure (integrity) | Forward-Post index+data write (`ProcessorPipeline.cs` `AtomicForwardWrite` / `:298-309`) | mitigate | Single atomic Lua `ScriptEvaluateAsync` (`const string AtomicForwardWrite`: HSET slot + whole-hash PEXPIRE + SET-with-PX) replaces the former 3 separate ops — no partial index-without-data / data-without-index window. TTLs computed in C#, passed as ARGV[4]/[5]. Verified `:96-100`, `:298-309`; tests `Completed_AllocationBeforeData`, `IndexTtl_IsRandom_*`. | closed |
| T-69-02 | Tampering / DoS (processor/keeper race on the index key) | Forward cleanup tail `DeleteTerminalAsync` (`:346-347`) | mitigate | Local `bool escalated` (`:286`) set once at the INJECT site (`:321`); cleanup tail gated `if (!escalated) DeleteTerminalAsync(...)` — when any item escalated, index + input keys left intact for keeper / Recovery / TTL. Recovery-pass delete (`:218`) correctly ungated. Test `EscalatedItem_SkipsCleanup` (`DidNotReceive().KeyDeleteAsync(RedisKey[], …)`, array overload stubbed → true never-called). | closed |
| T-69-03 | Denial of Service (lost-write / availability) | INFRA-01 drop path (former `:277-281`) | mitigate | Atomic-write exhaust routes to a single `SendKeeper(BuildInject(d, item, entryId), …)` (`:320`); the only `continue` is post-INJECT (`:323`). The bare INFRA-01 silent DROP is removed — no lost-write path remains. Tests `AtomicWriteFault_Inject` (`Assert.Single` KeeperInject + `Assert.Empty` StepCompleted), `WriteFault_Inject`. | closed |
| T-69-04 | Tampering (Lua injection) | The Lua script body (`AtomicForwardWrite`) | accept | Compile-time `private const string` (`:96`) — never interpolated/concatenated; all variable data crosses as parameterized `KEYS[]` / `RedisValue[] ARGV` (`:300-308`). Injection-safe Redis-Lua pattern (mirrors Phase-40). Premise confirmed in code. | closed |
| T-69-05 | DoS (key leak if skipped cleanup never reclaimed) | Skipped-cleanup keys (index + input) | accept | A TTL is always present on the atomic write (ARGV[4]=`SlotTtl()` ms, ARGV[5]=`executionDataTtl` ms, both unconditional). Both TTL derivations floored at `Math.Max(1, ExecutionDataTtlSeconds)` (`:116`, `:126`) preventing `PX 0`; skip path does not `KeyPersist`. Leak is TTL-bounded; Recovery reclaims idempotently on redelivery (GATE-02). Premise confirmed in code. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-69-01 | T-69-04 | The forward-Post Lua script is a compile-time `const` with parameterized KEYS/ARGV; no user data enters the script text. Standard injection-safe pattern, no further control warranted. | Phase plan disposition (69-01) | 2026-06-16 |
| AR-69-02 | T-69-05 | Keys left by a skipped (escalated) cleanup are bounded by the always-present index + data TTLs and idempotently reclaimed by the Recovery pass on redelivery; no unbounded leak. | Phase plan disposition (69-02) | 2026-06-16 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-16 | 5 | 5 | 0 | gsd-security-auditor |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-16
