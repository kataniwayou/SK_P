# Phase 16: Idempotency + concurrency + L1 cleanup + 3-GREEN closeout - Context

**Gathered:** 2026-05-29
**Status:** Ready for planning

<domain>
## Phase Boundary

Phase 16 is the **v3.3.0 closeout / verification phase** — it writes the four remaining
test-coverage requirements (TEST-REDIS-06..09), reconciles the docs that the Phase 15
Stop-semantics inversion left stale, and satisfies the v3.3.0 phase-close gate
(3 consecutive GREEN + byte-identical `psql \l` SHA-256 + byte-identical
`redis-cli --scan` SHA-256). **No new production behavior** — the orchestration pipeline
(L3→L1→L2, validation gates, Start per-workflow loop, Stop existence-gate + cleanup)
shipped in Phases 12–15.

**Critical inheritance — the Stop semantics were INVERTED in Phase 15.** The original
Phase 16 success criteria (and REQUIREMENTS.md TEST-REDIS-09) described Stop as a pure
existence check that *deletes nothing*. The 15-CONTEXT `<amendments>` reversed this: Stop
now **deletes the root + per-step keys** (NEVER processor keys), is **non-idempotent**
(repeat → 422), and Start is **delete-then-write** per workflow. Phase 15 already shipped
that behavior with passing facts. Phase 16 inherits the inverted contract and owns the
matching SC + REQUIREMENTS edits (flagged by Plan 15-05).

**Out of scope (deferred to a separate step):** milestone-close itself — PROJECT.md
"Validated" evolution, milestone archive, version bump — belongs to
`/gsd-complete-milestone`, run AFTER this phase satisfies the gate. Phase 16 only makes
the gate pass; it does not archive the milestone.

</domain>

<decisions>
## Implementation Decisions

### Test coverage strategy (TEST-REDIS-06..09)

- **D-01 — Concurrent-Start test = observational / non-flaky (locked 1a).**
  TEST-REDIS-08's concurrent regression fires two parallel `POST /api/v1/orchestration/start`
  for the SAME workflowId(s) and asserts: (1) both responses are 204, (2) no exception /
  process crash, (3) the final L2 state is **structurally valid** — all expected keys
  present, every value round-trips via `System.Text.Json`. It **documents** the
  last-write-wins / partial-interleave behavior (per the requirement wording) and does
  NOT assert a deterministic winner. Rationale: there is no Redis lock (last-write-wins by
  design — PROJECT.md); a strict "final state == exactly one writer's payload" assertion
  would flake under genuine key interleave. The test must be stable across the 3-GREEN
  cadence.

- **D-02 — Idempotency "second write reflected" is an explicit assertion (TEST-REDIS-08, sequential half).**
  Beyond the existing `StartLoopFacts.ReStart_Removes_Orphan_Step` (which proves orphan
  per-step keys are removed on re-Start), add an assertion that a Start-twice with the
  SAME workflowIds leaves L2 reflecting the SECOND write — e.g. a changed value (new
  `jobId` per Start is the cheapest observable delta; `jobId` is `Guid.NewGuid()` per Start
  per 15-CONTEXT D-05) confirms overwrite-not-append. May extend the existing re-Start fact
  or live in the new idempotency fact class (planner's discretion).

- **D-03 — Happy-path E2E = new dedicated full-HTTP fact (locked 3a).**
  TEST-REDIS-06 gets a NEW fact that drives the real HTTP→service→Redis path:
  `POST /start` (valid multi-workflow graph) → `redis-cli`/`IDatabase` GET on all three
  keyspaces (root `{prefix}{workflowId}`, per-step `{prefix}{workflowId}:{stepId}`,
  per-processor `{prefix}{processorId}`) → assert each via `System.Text.Json`
  deserialization round-trip against the locked DTO shapes (field names per 15-CONTEXT
  D-02: `entryStepIds`/`cron`/`jobId`/`liveness`/`correlationId` on root;
  `entryCondition`/`processorId`/`payload`/`nextStepIds` on step;
  `inputDefinition`/`outputDefinition`/`liveness` on processor). Rationale: the existing
  `RedisProjectionWriterFacts` exercise the writer in isolation, not the full HTTP path
  with `X-Correlation-Id` plumbing and the per-workflow Start loop.

- **D-04 — Gate-failure no-write proof = one consolidated new fact class (locked 4a).**
  TEST-REDIS-07 gets a single NEW fact class that drives all four failure types
  (cycle / missing-next-step / schema-edge mismatch / payload-vs-config-schema) and, for
  each, asserts 422 + RFC 7807 error body shape AND a **SCAN confirms zero L2 keys exist
  for the failed workflowId**. The existing Phase 14 gate facts (`CycleDetectionFacts`,
  `MissingStepFacts`, `SchemaEdgeFacts`, `PayloadConfigSchemaFacts`, `ValidationOrderFacts`)
  are **left untouched** — clean TEST-REDIS-07 ownership, no Phase 14 regression risk.
  SCAN-only (no `KEYS` / `IServer.Keys()`), reusing the `RedisFixture` per-class prefix.

### Doc reconciliation

- **D-05 — Doc-first amendment commit (locked 2a).**
  The FIRST plan in this phase is a doc-first amendment (Phase 11/15 precedent): rewrite
  **REQUIREMENTS.md TEST-REDIS-09** (drop "Stop is verified to NOT delete any L2 keys
  / post-Stop SCAN matches pre-Stop"; replace with the inverted "post-Stop root + per-step
  keys removed, processor keys intact") and **ROADMAP.md Phase 16 SC2/SC5** (resolve the
  flagged inversion NOTE — SC2/SC5 currently still describe the pre-inversion Stop). Then
  the facts follow the corrected text. This closes the only known doc/behavior drift in the
  milestone.

### Phase-close gate

- **D-06 — Reuse the Phase 12-08 gate scripts as-is.**
  `scripts/phase-12-close.ps1` and `scripts/phase-12-close.sh` already encode the dual-SHA
  gate (3-consecutive `dotnet test` GREEN + `psql \l` SHA-256 BEFORE=AFTER +
  `redis-cli --scan | sort | sha256sum` BEFORE=AFTER). Phase 16 reuses them (parameterize /
  copy as the planner sees fit) — no new gate tooling. The `redis-cli --scan` invariant
  holds because `RedisFixture.DisposeAsync` already SCAN-deletes the per-class prefix
  (verified — processor keys' 100-day TTL does not leak within a run; `FLUSHDB` stays
  forbidden).

### Claude's Discretion
- Whether D-02's idempotency assertion extends `ReStart_Removes_Orphan_Step` or lives in a
  new `IdempotencyFacts` / `ConcurrentStartFacts` class.
- Exact new test class names, file placement under `tests/BaseApi.Tests/Features/Orchestration/`
  (and/or `Observability/` for the E2E), and `[Trait]` tagging.
- The specific observable delta used to prove "second write reflected" (D-02).
- Whether the full happy-path E2E (D-03) reuses `OrchestrationLogsE2ETests` infra or stands
  up its own fixture.
- Whether the gate scripts are copied to `phase-16-close.*` or reused with a parameter.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements & roadmap (read WITH the Phase 15 amendments applied on top)
- `.planning/REQUIREMENTS.md` — TEST-REDIS-06, TEST-REDIS-07, TEST-REDIS-08, TEST-REDIS-09
  (line items 148–151). **TEST-REDIS-09 text is STALE** — the "Stop NOT delete / post-Stop
  SCAN matches pre-Stop" clause is reversed by 15-CONTEXT `<amendments>`; D-05 rewrites it.
- `.planning/ROADMAP.md` §"Phase 16" — SC1..SC5 + the flagged NOTE (line 125) declaring
  SC2/SC5 INVERTED; "v3.2.0 invariants MUST NOT regress" block (line 127:
  `FLUSHDB` forbidden, `KEYS`/`IServer.Keys()` forbidden, 142/142 baseline must stay GREEN).

### The authoritative Stop/Start contract this phase tests
- `.planning/phases/15-l2-redis-projection-write-stop-existence-check/15-CONTEXT.md`
  `<amendments>` + `<decisions>` D-04/D-05/D-06/D-07 — the INVERTED Stop semantics, the
  `jobId = Guid.NewGuid()` per Start, the locked L2 field shapes, the delete-then-write
  Start loop. **Authoritative where it conflicts with REQUIREMENTS.md/ROADMAP.md original text.**

### Existing test assets (extend / mirror — do NOT duplicate)
- `tests/BaseApi.Tests/Features/Orchestration/StartLoopFacts.cs` — `Start_Returns204`,
  `ReStart_Removes_Orphan_Step`, `Start_RedisDown_500` (idempotency base — D-02 extends).
- `tests/BaseApi.Tests/Features/Orchestration/StopGateFacts.cs` /
  `StopCleanupFacts.cs` / `StopOrchestrationFacts.cs` — Stop 204/422/repeat/delete-root-
  keep-processor/dangling-skip/cyclic-terminate (covers most of TEST-REDIS-09 in inverted form).
- `tests/BaseApi.Tests/Features/Orchestration/CycleDetectionFacts.cs`,
  `MissingStepFacts.cs`, `SchemaEdgeFacts.cs`, `PayloadConfigSchemaFacts.cs`,
  `ValidationOrderFacts.cs` — the four gate 422s (TEST-REDIS-07 adds the SCAN-no-write
  layer on top, in a NEW class per D-04; leave these untouched).
- `tests/BaseApi.Tests/Features/Orchestration/RedisProjectionWriterFacts.cs` +
  `Projection/ProjectionRecordRoundTripTests.cs` — writer-level 3-keyspace + TTL +
  round-trip (D-03 adds the full-HTTP path on top).
- `tests/BaseApi.Tests/Observability/OrchestrationLogsE2ETests.cs` — E2E pattern the
  happy-path fact (D-03) may reuse.
- `tests/BaseApi.Tests/Composition/RedisFixture.cs` — per-class `test:cls-{Guid:N}:`
  prefix + `DisposeAsync` SCAN-delete + zero-residual assert (the gate-enabling cleanup).

### Phase-close gate tooling
- `scripts/phase-12-close.ps1` + `scripts/phase-12-close.sh` — the dual-SHA + 3-GREEN gate
  (D-06 reuse).

### No external specs
- No external ADRs/design docs (v3.2.0 ADRs archived under `milestones/`). REQUIREMENTS.md +
  ROADMAP.md + 15-CONTEXT.md + this CONTEXT are the spec of record.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `RedisFixture` (per-class prefix isolation + SCAN-delete teardown) — every new Redis-touching
  fact must use it; teardown already guarantees the `redis-cli --scan` SHA-256 invariant.
- `OrchestrationLogsE2ETests` — full-HTTP E2E scaffold for the D-03 happy-path fact.
- `Phase8WebAppFactory` (+ Redis-attached successor) — real Postgres + real Redis boot.
- `RedisProjectionKeys` — single source of truth for all 3 key formats (use it in test
  GET/SCAN assertions, do not hand-format keys).
- The 4 projection records (`WorkflowRootProjection`/`StepProjection`/`ProcessorProjection`/
  `LivenessProjection`) — `[JsonPropertyName]`-pinned, round-trippable for D-03 deserialization.

### Established Patterns
- 3-consecutive-GREEN phase-close cadence (Phase 3 D-18) — honored 14× this project.
- Byte-identical `psql \l` SHA-256 `0d98b0de…0aac127` (Phase 3 D-15) — would be the 5th
  consecutive phase to record this baseline.
- Doc-first amendment as plan #1 when a phase changes locked requirement text (Phase 11/15).
- SCAN-only enumeration in both production and tests (`KEYS`/`IServer.Keys()` forbidden).

### Integration Points
- New fact classes land under `tests/BaseApi.Tests/Features/Orchestration/` (unit/integration)
  and optionally `tests/BaseApi.Tests/Observability/` (full-HTTP E2E).
- No `src/` changes expected — this is a test + docs + gate phase. If a fact surfaces a real
  production bug, that is a fix-forward deviation, not planned scope.

</code_context>

<specifics>
## Specific Ideas

- "lock all four as recommended, go" — concurrent test observational/non-flaky (1a),
  doc-first amendment (2a), dedicated full-HTTP happy-path E2E (3a), consolidated
  TEST-REDIS-07 SCAN-no-write fact class (4a).
- Reuse Phase 12-08 gate scripts as-is; milestone-close (PROJECT.md evolution / archive) is a
  SEPARATE `/gsd-complete-milestone` step run after the gate passes.

</specifics>

<deferred>
## Deferred Ideas

- **Milestone-close** (PROJECT.md "Validated" updates, milestone archive to
  `milestones/v3.3.0-ROADMAP.md`, version bump) — `/gsd-complete-milestone`, after the gate.
- **Strict last-write-wins concurrency assertion** — rejected as flaky (D-01); revisit only
  if a Redis lock / `generationId` is ever introduced (PROJECT.md lists `generationId` as a
  forward-compat candidate when Scheduler multi-writer races appear).
- Carried forward from 15-CONTEXT (still deferred): `liveness.status` Start/Stop lifecycle,
  `stopCorrelationId`, processor-key eviction/GC, real `jobId`/`liveness.interval` semantics,
  OBSERV-REDIS-04 Redis metrics.

</deferred>

---

*Phase: 16-idempotency-concurrency-l1-cleanup-3-green-closeout*
*Context gathered: 2026-05-29*
