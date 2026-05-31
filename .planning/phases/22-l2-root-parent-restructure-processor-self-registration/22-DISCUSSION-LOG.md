# Phase 22: L2 Root-Parent Restructure + Processor Self-Registration Boundary - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-31
**Phase:** 22-l2-root-parent-restructure-processor-self-registration
**Mode:** discuss (prose confirm-loops per user preference; SPEC.md loaded — 5/6 requirements; implementation-decisions only)
**Areas discussed:** Index key strings, Member-set/cleanup wiring, Processor-liveness validator, Prefix const + test isolation, L2IDX-02 amendment

---

## Fork 1 — Index key strings & builders

| Option | Description | Selected |
|--------|-------------|----------|
| Bare prefix for parent index | `skp:` (the const itself) as the Redis SET key | ✓ |
| Separate `skp:{wf}:members` member-set key | New `Members(Guid)` builder | (initially proposed, later dropped — see Fork 5) |

**User's choice:** Bare prefix for the index. One single prefix const for ALL keys (root, child, index, processor) — "no two prefixes."
**Notes:** Confirmed twice that `{prefix}` is one const = `skp:`, prepended uniformly. No member-set builder (resolved via the L2IDX-02 drop).

---

## Fork 2 — Cleanup wiring

| Option | Description | Selected |
|--------|-------------|----------|
| Cleanup is the complete per-wf teardown | `SREM` wf from parent index + `DEL` root + `DEL` steps (BFS) | ✓ |
| Pre-clean leaves the parent index alone | Re-Start re-`SADD`s idempotently | |

**User's choice:** Cleanup also removes the specific wf from the parent index (`SREM` the one id, not `DEL` the whole index). Step ids found "always by traversal" via `entryStepIds → nextStepIds`.
**Notes:** Pulls `SREM` into Phase 22 scoped to the cleanup routine; the Stop *flow* stays Phase 23. Cleanup keeps Redis BFS (runs before L1 load; must catch stale shrunk-graph keys).

---

## Fork 3 — Processor-liveness validator

| Option | Description | Selected |
|--------|-------------|----------|
| New async validator after the 3 sync gates | `ValidateAsync(snapshot, ct)`, reads each `skp:{procId}`, 422 on absent/stale | ✓ |
| Fold into the edge validator | | rejected — PROC-EDGE-01 keeps edge validation decoupled/unchanged |

**User's choice:** "Analyze how the edge validator works" → use that analysis to shape it. Result: separate async validator (edge validator is sync/in-memory; liveness needs Redis), one factory `ProcessorNotLive(procId, reason)` with absent/stale reason, same 422 path.
**Notes:** Reads `liveness.interval` from the entry (not hardcoded 0); `now` from injected `TimeProvider`. Iterates the in-memory `snapshot.Processors`; only Redis touch is per-processor liveness `GET`.

---

## Fork 4 — Prefix const + test isolation

| Option | Description | Selected |
|--------|-------------|----------|
| `public const string Prefix` | Mandatory — it IS L2PREFIX-01 | ✓ (locked) |
| (1) Logical Redis DB per test class | Total isolation, `FLUSHDB` teardown; gate scans only DB0 | |
| (2) Stay on DB0 | GUID-unique keys + parent-index tests serialized; delete-known-keys cleanup; gate scans the real keyspace | ✓ |

**User's choice:** Const is mandatory. Test isolation = **Option 2** (stay on DB0).
**Notes:** Chosen to keep the triple-SHA close gate honestly scanning the keyspace the tests use. Per-class cleanup moves from prefix-scan to delete-known-keys; parent-index tests run in one non-parallel collection.

---

## Fork 5 — L2 structure / L2IDX-02

| Option | Description | Selected |
|--------|-------------|----------|
| (a) Separate member SET `skp:{wf}:members` | SPEC-faithful Redis SET of all member step ids | |
| (b) Drop L2IDX-02 | Parent index + today's root(entry steps)/step structure; enumeration via traversal | ✓ |

**User's choice:** "Don't create it. Confirm." — drop the member set. Final structure = today's root/step + the single new parent index `skp:`. Root `stepIds` are entry steps only.
**Notes:** SPEC.md amended (requirement removed, acceptance line dropped, "member lists are SETs" constraint relaxed, Amendments section added). User approved the SPEC edit.

---

## Claude's Discretion

- Exact wave-vs-recursive cleanup BFS form (mirror existing `RedisL2Cleanup`).
- Keep vs prune the now-unused `ProcessorKeyTtlDays`.
- Batch/pipeline grouping of new `SADD`/`SREM` with existing writes/deletes.
- New-validator DI registration ordering.

## Deferred Ideas

- L2IDX-02 member SET (revisit if no-traversal enumeration is ever needed).
- Phase 23: orchestrator reload/stop lifecycle, SREM-on-Stop flow, publish jobIds.
- Processor self-registration write path; Quartz/scheduler.
