---
phase: 70-processor-inject-cleanup
plan: 02
subsystem: testing
tags: [keeper-recovery, nsubstitute, redis, messaging-contracts, xunit-v3, refactor]

# Dependency graph
requires:
  - phase: 70-processor-inject-cleanup (plan 01)
    provides: "Non-destructive InjectConsumer + reduced KeeperInject contract (DeleteEntryId dropped from src/) — the shape these tests assert against"
provides:
  - "KeeperDeleteInvariantFacts — a behavioral, build-breaking guarantee that DELETE is the only keeper state that deletes keys (DeleteConsumer deletes both keys; Inject/Reinject do not, both KeyDeleteAsync overloads, each with a positive side-effect co-assertion)"
  - "Reflection negative guard: re-adding KeeperInject.DeleteEntryId fails KeeperContractTests AND fails to compile"
  - "Four reshaped fact files (InjectConsumerFacts, KeeperContractTests, PipelineForwardFacts, SC2 E2E) on the reduced id-set; solution restored to a 0-warning Release+Debug build"
affects: [71-orchestrator-recovery-pipeline, keeper-delete-invariant]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Behavioral cross-consumer invariant: construct the REAL consumer over RecoveryTestKit.Db() and assert DidNotReceive on BOTH KeyDeleteAsync overloads, co-asserted with a positive captured-send so a no-op cannot pass"
    - "Reflection Assert.Null negative guard makes a dropped contract field a build-breaking re-introduction tripwire"

key-files:
  created:
    - tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs
  modified:
    - tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs
    - tests/BaseApi.Tests/Contracts/KeeperContractTests.cs
    - tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs
    - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs

key-decisions:
  - "New invariant fact is behavioral (D-04) and scoped to the three RecoveryConsumerBase consumers — L2ProbeRecovery is NEVER instantiated, keeping its :35 scratch delete structurally outside the invariant (Pitfall 3 carve-out)"
  - "InjectConsumerFacts collapses the Received.InOrder chain (now meaningless with one db call) to a direct write-Received + captured StepCompleted + a DidNotReceive-delete belt on both overloads (Pitfall 5)"
  - "SC2 E2E delete-half removed as a WHOLE block (deleteEntryId/deleteKey seed+register, DeleteEntryId field, sourceDeleted PollForKeyAbsent assertion) per Pitfall 4 — a literal 3-line removal would not compile"
  - "D-10 build gate (0-warning Release + Debug) met; the FULL-suite-green half of D-10 is operator/live-stack-gated, not achievable on a bare hermetic dotnet test (see Deviations)"

patterns-established:
  - "Delete invariant as one readable fact file: DELETE deletes (positive RedisKey[] Received); INJECT + REINJECT do not (negative DidNotReceive both overloads + positive side-effect co-assertion)"
  - "Contract field-removal tripwire via reflection Assert.Null"

requirements-completed: [KINJ-02, KINJ-03]

# Metrics
duration: 26min
completed: 2026-06-16
---

# Phase 70 Plan 02: Test Reshape + Delete-Invariant Lock Summary

**Reshaped four keeper test files to the reduced `KeeperInject` shape and added `KeeperDeleteInvariantFacts` — a behavioral, build-breaking proof that DELETE is the only keeper state that deletes keys — restoring a 0-warning Release+Debug build after Plan 01 dropped the `DeleteEntryId` field.**

## Performance

- **Duration:** ~26 min
- **Started:** 2026-06-16T05:38:20Z
- **Completed:** 2026-06-16T06:04:41Z
- **Tasks:** 3
- **Files modified:** 4 (+1 created)

## Accomplishments
- `KeeperDeleteInvariantFacts.cs` (KINJ-03) — three behavioral `[Fact]`s: `DeleteConsumer_deletes_both_keys` (positive `RedisKey[]` two-key DEL), `InjectConsumer_never_deletes` and `ReinjectConsumer_never_deletes` (negative `DidNotReceive` on BOTH `KeyDeleteAsync` overloads, each co-asserted with a captured `StepCompleted`/`EntryStepDispatch` send so a silent no-op cannot pass). `L2ProbeRecovery` is never instantiated (carve-out). All 3 pass.
- `KeeperContractTests` (KINJ-02) — INJECT fact renamed to the reduced id-set, `DeleteEntryId` positive block replaced with `Assert.Null(typeof(KeeperInject).GetProperty("DeleteEntryId"))`; re-adding the field now fails this fact AND fails to compile. 6/6 pass.
- `InjectConsumerFacts` (D-07) — `DeleteEntryId` removed from the message; `Received.InOrder` chain + positive delete assertion dropped; surviving order expressed as write-Received (5-arg SE.Redis shape) + single captured `StepCompleted` + a `DidNotReceive`-delete belt on both overloads; method + class doc rewritten. 3/3 pass.
- `PipelineForwardFacts` (D-08) — `inj.DeleteEntryId` assertion deleted; NODROP-01 doc trimmed. 8/8 pass.
- `SC2RecoveryPathsE2ETests` (D-09) — the whole INJECT delete-half removed (compile-only RealStack fact); data-key write assertion kept; STATE-3 doc comments rewritten to "writes the data key + sends StepCompleted, deletes nothing."
- Repo-wide `DeleteEntryId` now appears ONLY as the `KeeperContractTests` negative guard + its doc sentence — zero functional references in `src/` or `tests/`.
- Solution builds **0-warning / 0-error in BOTH Release and Debug** (D-10 build half).

## Task Commits

Each task was committed atomically:

1. **Task 1: Add the dedicated delete-invariant fact (KINJ-03)** - `52c0780` (test)
2. **Task 2: Reshape InjectConsumerFacts + KeeperContractTests reflection guard (D-07/D-06)** - `b110f67` (test)
3. **Task 3: Reshape PipelineForwardFacts + SC2 E2E delete-half + dual-config build gate (D-08/D-09/D-10)** - `2292651` (test)

## Files Created/Modified
- `tests/BaseApi.Tests/Keeper/KeeperDeleteInvariantFacts.cs` (NEW) — the KINJ-03 cross-consumer invariant fact; reuses `RecoveryTestKit`; `[Trait("Phase","70")]`.
- `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` — non-destructive write-then-send shape + both-overload delete belt.
- `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs` — reduced-id-set INJECT fact + `Assert.Null` field-removal tripwire.
- `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` — dropped the `inj.DeleteEntryId` assertion + doc.
- `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` — INJECT delete-half removed (compiles; RealStack/live-only).

## Decisions Made
- Followed D-04 through D-10 verbatim, including the RESEARCH Pitfall guidance: both `KeyDeleteAsync` overloads asserted (Pitfall 2), `L2ProbeRecovery` never touched (Pitfall 3), the full SC2 delete-block removed rather than 3 literal lines (Pitfall 4), the `Received.InOrder` collapse (Pitfall 5), and the PipelineForward doc trim (Pitfall 6).
- Left the explanatory `// ... Received.InOrder no longer guards anything (Pitfall 5)` comment in `InjectConsumerFacts` (rationale text only — the code construct is gone); the acceptance criterion targets the code construct, which is removed.

## Deviations from Plan

### Note (not an auto-fix): D-10 "full suite green" is live-stack-gated, not hermetic

- **Found during:** Task 3 (D-10 build/test gate)
- **Observation:** The plan's D-10 / success criteria state the "full `BaseApi.Tests` suite" must be green on a bare `dotnet test`, with RealStack E2E "excluded automatically by `Category!=RealStack`." In this repo that exclusion is NOT automatic — `BaseApi.Tests.csproj` carries no default trait filter, so a bare `dotnet test tests/BaseApi.Tests` runs ALL tests. A large set of integration tests (Redis `localhost:6380/6379/6399`, Postgres `5433`, RabbitMQ `5673`, OTLP `4317`) require the live docker-compose stack, which is the operator-gated close-gate context (`scripts/*close*.ps1` run "full suite, RealStack E2E run live"), not a hermetic developer run.
- **Evidence it is environmental, not caused by this plan:**
  - Bare full suite: **288 failed / 488 passed**. Adding `--filter-not-trait "Category=RealStack"`: **272 failed / 488 passed** (only 16 RealStack-tagged dropped) — the remaining 272 are untagged integration tests needing live infra.
  - Port probe confirmed Redis `6380`, Postgres `5433`, RabbitMQ `5673` all CLOSED (only k8s/kind containers were up; no compose stack).
  - A representative Redis-fixture class I did NOT touch (`*RedisProjection*`) fails 4/9 on the same connection-refused cause.
  - Every file THIS plan touched passes green in scoped hermetic runs (no infra): `KeeperDeleteInvariant` 3/3, `InjectConsumerFacts` 3/3, `KeeperContractTests` 6/6, `PipelineForward` 8/8.
- **Disposition:** Out of scope per the executor SCOPE BOUNDARY (only auto-fix issues directly caused by this task's changes; pre-existing failures in unrelated files are not). No source/test edits were made to chase them. The substantive D-10 half that IS hermetic — the **0-warning Release + Debug build** — passes. The "full suite green" half requires the live stack (operator-gated, the same context Phase 55/62/68 close gates run in) and is deferred to that run, consistent with how this repo's integration suite has always been gated.

---

**Total deviations:** 0 auto-fixed. 1 documented gate-scope note (D-10 full-suite-green is live-stack-gated; the hermetic build half passes).
**Impact on plan:** All in-scope work (5 files + the new invariant fact + 0-warning dual-config build) is complete and green hermetically. The full-suite-green criterion is an environment/harness condition, not a code defect introduced here.

## Issues Encountered
- The combined MTP filter `--filter-method "*InjectConsumerFacts*|*KeeperContractTests*"` reported a failure while each pattern run **separately** passed (Inject 3/3, Contract 6/6) — the `|`-combined glob over-matches into the infra-dependent suite. Resolved by running the two scopes individually (both green) per the MTP filter quirk (auto-memory: MTP filter syntax). Not a defect in the edited files.

## Known Stubs
None.

## Next Phase Readiness
- KINJ-02 and KINJ-03 are locked: the delete invariant is now behaviorally build-breaking and the dropped field is a reflection tripwire. Phase 71 (Orchestrator Recovery Pipeline) can mirror the non-destructive-INJECT pattern with this invariant as its regression guard.
- Open environment item (carried, not introduced here): the hermetic developer run of `BaseApi.Tests` needs the docker-compose infra (Redis/Postgres/RabbitMQ) for the integration suite to go green; the operator close-gate already runs the full suite live.

## Self-Check: PASSED

All 5 touched files exist; all 3 task commits (`52c0780`, `b110f67`, `2292651`) are present in git history; the new invariant fact's 3 facts pass and the four reshaped files pass green in scoped hermetic runs; Release + Debug builds are 0-warning.

---
*Phase: 70-processor-inject-cleanup*
*Completed: 2026-06-16*
