---
phase: 54
slug: terminal-index-delete
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-12
---

# Phase 54 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.
> Built from the 4 PLAN.md `<threat_model>` blocks (State B). Phase 54 is an internal
> Redis key-lifecycle behavior change (design amendment A19) plus hermetic test edits —
> it introduces NO new external attack surface: no new input parsing, no new auth/network
> boundary, no new secret. All keys/ids are internal GUIDs already in the trusted message
> envelope. No high/critical threats are introduced.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| processor tail → Redis (`sk-redis`) | The atomic two-key `DEL` crosses to single-instance trusted Redis. | Internal allocation/data GUID keys (`ExecutionData(entryId)`, `MessageIndex(messageId)`) — no PII |
| processor → keeper bus (`KeeperDelete` envelope) | The DELETE escalation crosses the in-cluster RabbitMQ bus; the keeper re-issues the same both-key DEL. | Internal GUIDs already in the trusted message envelope (now incl. `MessageId`) — no PII |
| (none — test layer) | Plans 54-01 / 54-04 edit hermetic NSubstitute/xUnit doubles only — no production code, no runtime surface. | — |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-54-01 | Tampering | Test-kit mock fidelity (false-green) — `DispatchTestKit` fault muxes | mitigate | Array `KeyDeleteAsync(RedisKey[])` overload stubbed/thrown on EVERY fault mux so an unstubbed `Task<long>`→`0L` cannot silently mask the escalation branch (RESEARCH Pitfall 1). Verified: all fault muxes (`PresentReadWriteFaultL2`, `ForwardDataFaultL2`, `ForwardSlotFaultL2`, `ReadOkDeleteFaultL2`, `ReadOkDeleteAndPersistFaultL2`) stub the array overload after `/gsd-code-review-fix 54 --all` (WR-01/02 + IN-01 resolved). Test-correctness, not a security defect — recorded for completeness. | closed |
| T-54-02 | Information Disclosure | `KeeperDelete.MessageId` on the bus | accept | Internal allocation-index GUID already present in the trusted envelope; carries no PII, never externally exposed; in-cluster bus + single-replica trusted `sk-redis`. | closed |
| T-54-03 | Tampering | Spoofed `MessageId` → unintended index DELETE | accept | Worst case is an at-least-once duplicate (A16), not a security defect; the bus is internal/trusted; no new external input path. | closed |
| T-54-04 | Tampering | Two-key `DEL` operating on internal GUID keys | accept | Operands are internal allocation/data GUIDs already in the trusted envelope; single-instance trusted `sk-redis`; no external input path; worst case A16 duplicate. | closed |
| T-54-05 | Denial of Service | Persist-exhaust blocking the keeper handoff | mitigate | D-03: persist is best-effort and `SendKeeper` is UNCONDITIONAL after the persist attempt — a persist failure can never stall the escalation; index also retains its random-TTL backstop (D-07). Verified: `ProcessorPipeline.DeleteTerminalAsync` (no short-circuit) + `EndDelete_PersistExhaust_StillSendsKeeper` fact green. | closed |
| T-54-06 | Repudiation | Index left un-reclaimed on a non-crash path (the A18 net-zero gap A19 closes) | mitigate | Active two-key DEL deterministically reclaims the index; on DEL exhaustion the keeper both-key DEL is the durable GC; retained TTL (D-07) is the crash-before-delete backstop. Verified: GC-01 delete facts green; D-07 `KeyExpireAsync` writes confirmed present. | closed |
| T-54-07 | Tampering | Atomicity assertion fidelity (two-scalar false pass) | mitigate | Every delete fact pairs `Received(1)` on the array overload with `DidNotReceive()` on BOTH scalar overloads (RESEARCH Pitfall 2) — a regression to two scalar deletes fails the suite. Verified: PipelineEndDeleteFacts 7/7, PipelineRecoveryFacts 5/5, DeleteConsumerFacts 2/2, PipelinePreFacts 4/4 green. Test-correctness — recorded for completeness. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-54-01 | T-54-02 | `MessageId` is an internal allocation-index GUID already in the trusted message envelope — no PII, never externally exposed; in-cluster bus + single-replica trusted `sk-redis`. | Phase 54 threat model (locked in 54-02-PLAN.md) | 2026-06-12 |
| AR-54-02 | T-54-03 | A spoofed `MessageId` would at worst cause an at-least-once duplicate (accepted under A16) — not a security defect; no new external input path is introduced. | Phase 54 threat model (locked in 54-02-PLAN.md) | 2026-06-12 |
| AR-54-03 | T-54-04 | The two-key `DEL` operates on internal allocation/data GUIDs already in the trusted envelope against single-instance trusted `sk-redis`; no external input path; worst case A16 duplicate. | Phase 54 threat model (locked in 54-03-PLAN.md) | 2026-06-12 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-12 | 7 | 7 | 0 | /gsd-secure-phase (State B, ASVS L1) — mitigations cross-verified against gsd-verifier (8/8), code review (production clean), and code-review-fix (4/4 findings resolved) this session |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-12
