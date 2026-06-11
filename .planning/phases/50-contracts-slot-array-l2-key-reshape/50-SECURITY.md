---
phase: 50
slug: contracts-slot-array-l2-key-reshape
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-11
---

# Phase 50 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

Outcome: **SECURED** — all 5 threats have verified dispositions; 0 open; 0 unregistered SUMMARY flags. Net security posture for this phase: attack surface **reduced** (a composite-backup key class + its consumers were removed). No new I/O, endpoint, or deserialization path was introduced — Phase 50 is a contract-shape addition + Model-B removal with no new runtime behavior.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| (none new — 50-01) | Pure string-format key builder + three `init` properties on an existing internal-bus wire record. | A typed CLR `Guid` (the only `MessageIndex` input); no new external surface. |
| (boundary REDUCED — 50-02) | A trust-relevant surface was DELETED (the composite-backup key + its UPDATE/CLEANUP consumers). No new boundary introduced. | The keeper still consumes internal-bus recovery messages on `keeper-recovery` — unchanged, just fewer message types. |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-50-01 | I (Information Disclosure) | `MessageIndex(Guid)` key string | accept | Pure format `skp:msg:{guid:D}` over a typed Guid — only input is a CLR Guid, no caller-supplied string interpolated, no secret material. Evidence: `L2ProjectionKeys.cs:48`. | closed |
| T-50-02 | T (Tampering) | `KeeperInject.Data` raw-JSON field | accept | Additive `init`-default property on an existing contract; no endpoint reads/acts on it this phase (population Phase 51, consumption Phase 52). Evidence: `KeeperInject.cs:13`, `InjectConsumer.cs:21-22` (no-op stub). | closed |
| T-50-03 | I (Information Disclosure) | Deleted composite-backup key `skp:{corr}:{wf}:{proc}:{exec}` | mitigate (by removal) | `CompositeBackup` builder + its 2-day TTL deleted — one fewer L2 key class holding correlated workflow identifiers. 0 src references; live keys self-expire via TTL (no migration). Evidence: 0 `src/` matches; reflection guard `ModelBContractsRetiredFacts.cs:40-47`. | closed |
| T-50-04 | D (Denial of Service) | Survivor consumer stubs (no-op, not throwing) | accept | Shape-preserving no-ops with no I/O — cannot loop, allocate unboundedly, or amplify load. Real bounded/gated bodies return in Phases 51/52. Evidence: `InjectConsumer.cs:21-22` (`=> Task.CompletedTask`); `RecoveryConsumerBase.cs:26-76`; zero `NotImplementedException` in `src/Keeper/Recovery/`. | closed |
| T-50-05 | T (Tampering) | `ReinjectConsumerDefinition` partitioner re-home | accept | `PartitionKey`/`PartitionGuid` moved VERBATIM (byte-pinned, SHA256 over the 4-tuple, first 128 bits) — no algorithm change, no slot-drift. Single-owner invariant preserved (exactly 3 `UsePartitioner<T>`). Evidence: `ReinjectConsumerDefinition.cs:64-66,74-84`; `RecoveryPartitionFacts.cs:39,44,49,69,74`. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-50-01 | T-50-01 | Key format `skp:msg:{guid:D}` is a non-secret identifier; only input is a CLR Guid (no injection vector); no sensitive material embedded. | gsd-security-auditor (verified) | 2026-06-11 |
| AR-50-02 | T-50-02 | `KeeperInject.Data` is an additive `init`-defaulted field on an existing internal-bus contract; no endpoint reads/acts on it in Phase 50 (population Phase 51, consumption Phase 52); A16 at-least-once/no-dedup posture unchanged. | gsd-security-auditor (verified) | 2026-06-11 |
| AR-50-03 | T-50-04 | All three survivor consumer bodies are `Task.CompletedTask` no-ops or bounded gate-wait + bounded RetryLoop; no unbounded I/O or amplification path introduced. Real A18 bodies (Phases 51/52) get their own security review. | gsd-security-auditor (verified) | 2026-06-11 |
| AR-50-04 | T-50-05 | `PartitionKey`/`PartitionGuid` moved verbatim (byte-pinned by `RecoveryPartitionFacts`); no algorithm change, no new input, no slot-drift; single-owner invariant enforced by definition structure. | gsd-security-auditor (verified) | 2026-06-11 |

*T-50-03 is a mitigate-by-removal (not an accepted risk) — the attack surface was deleted, verified by reflection guard.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-11 | 5 | 5 | 0 | gsd-security-auditor (sonnet) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-11
