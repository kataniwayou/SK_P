# Phase 65: Fan-Out Workflow Seeder & Clean-State Stack - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-14
**Phase:** 65-fan-out-workflow-seeder-clean-state-stack
**Areas discussed:** Seeder artifact form, Reset routine structure, Heal-wait signal, Number seed values, Bring-up wiring

> SPEC.md present (4 requirements locked: WF-01, WF-02, ENV-01, ENV-02). Discussion focused on HOW to implement, not WHAT to build.

---

## Seeder artifact form

| Option | Description | Selected |
|--------|-------------|----------|
| C# RealStack E2E fixture | Extend existing internal-static seed helpers; reuse contracts/idempotency; invoke via `dotnet test --filter` | ✓ |
| PowerShell script (Invoke-RestMethod) | Mirror close-scripts; one harness language; re-encodes API contract in PS (drift risk) | |
| C# console / dotnet tool | New standalone project referencing contracts; more csproj/Dockerfile scaffolding | |

**User's choice:** C# RealStack E2E fixture
**Notes:** Reuses the contract-bearing `SeedProcessorAsync`/`SeedStepAsync`/`SeedWorkflowAsync` helpers; avoids API-contract duplication consistent with the codebase's anti-desync discipline.

### Follow-up: Self-verifying?

| Option | Description | Selected |
|--------|-------------|----------|
| Yes — self-verifying seeder | Embed WF-01/WF-02 acceptance queries + run-twice idempotency assertions after seeding | ✓ |
| No — pure seeding action only | Leave verification to Phase 66 or a separate test | |

**User's choice:** Yes — self-verifying seeder
**Notes:** Phase 65 delivers both the artifact AND its WF-01/WF-02 proof in one runnable fact.

---

## Reset routine structure

| Option | Description | Selected |
|--------|-------------|----------|
| Standalone PowerShell script | `scripts/phase-65-reset.ps1`: FLUSHALL → heal-wait → psql DELETE graph rows+junctions (preserve processors/schemas) → docker ps assert + orphan removal; stack stays up | ✓ |
| C# RealStack fixture | Redis admin FLUSHALL + Npgsql DELETE + shell-out docker ps; one language; docker ops awkward | |
| Hybrid (C# data + PS docker) | C# for redis+pg, PS wrapper for docker assertion; two coupled files | |

**User's choice:** Standalone PowerShell script
**Notes:** All three reset ops are shell-native and match the 18-script close-gate convention.

### Follow-up: Heal-wait signal

| Option | Description | Selected |
|--------|-------------|----------|
| Liveness keys reappear | Poll `redis-cli --scan skp:proc:*` until ≥1 fresh sample instance key; bounded timeout, fail loud | ✓ |
| Container / HTTP health probes | Poll docker health / `/ready` endpoints; coarser, can be ready before L2 key written | |
| Both keys + health | Poll liveness keys AND health endpoints; strongest, more script | |

**User's choice:** Liveness keys reappear
**Notes:** Exactly the L2 state the orchestration-start liveness gate reads — directly proves seed+activate won't 422.

---

## Number seed values

| Option | Description | Selected |
|--------|-------------|----------|
| Per-node distinct 1..9 | A=1, B=2, C=3, D1=4, E1=5, F1=6, D2=7, E2=8, F2=9 (SPEC label order) | ✓ |
| All same constant (0) | Logged sum = pure random addend; fully symmetric | |
| All same constant (100) | Sums land 100-199; obvious seed-applied marker; symmetric | |

**User's choice:** Per-node distinct 1..9
**Notes:** Value immaterial to correctness; distinct base addends make each step's log line self-distinguishing for per-correlationId trace debugging.

---

## Bring-up wiring

| Option | Description | Selected |
|--------|-------------|----------|
| Standalone scripts/phase-65-up.ps1 | `docker compose up -d` → wait 10 service types healthy → assert 0 badconfig; run once; harness-callable | ✓ |
| Documented command only | README prose; harness can't invoke a documented step | |
| Fold bring-up into reset script | One script does up+reset; conflates ENV-01 (start once) with ENV-02 (reset per-run) | |

**User's choice:** Standalone scripts/phase-65-up.ps1
**Notes:** badconfig already profile-gated → excluded with no compose edit; `processor-sample` keeps replicas:2.

---

## Claude's Discretion

- DAG edge-wiring mechanism (reverse-topo create with `NextStepIds` vs create-then-PUT).
- Assignment→step attachment via `AssignmentController` / `workflow_assignments`.
- Sentinel workflow name (example `v8-fanout-proof`).
- FK-safe DELETE ordering, psql invocation, heal-wait timeout/poll values.
- Per-service-type health-readiness probe specifics in bring-up.

## Deferred Ideas

None — discussion stayed within phase scope.
