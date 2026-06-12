# Phase 55: Live Proof & Close Gate - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-12
**Phase:** 55-live-proof-close-gate
**Areas discussed:** E2E reshaping strategy, Keeper-state live coverage (recovery pass), Net-zero scan deltas, Build-before-proof split & operator gate

---

## E2E reshaping strategy

| Option | Description | Selected |
|--------|-------------|----------|
| Hybrid | Retag SC3 as-is (A14 retained); adapt SC1 (+slot-array index +A19 net-zero); rewrite SC2 (3-state, both-key DELETE) + add organic recovery-pass test | ✓ |
| Rewrite all 3 fresh | New v5-named E2E tests, retire the v4 SC1/SC2/SC3 | |
| Adapt all 3 in place, no new test | Edit in place; SC1 round-trip + SC2 direct-publish treated as sufficient, no organic recovery test | |

**User's choice:** Hybrid (Recommended)
**Notes:** SC3 (BIT-gate pause/resume) is the lowest-risk adaptation since A14 is retained unchanged; SC1 needs the slot-array index write + A19 net-zero; SC2 + a new organic recovery test carry the v5-new behavior.

---

## Keeper-state live coverage (recovery pass)

| Option | Description | Selected |
|--------|-------------|----------|
| Organic recovery + state direct-publish | Real recovery (populated slot-array → re-fire → HGETALL→re-send→retire + A19 net-zero) PLUS per-state direct-publish (REINJECT present/absent, INJECT, DELETE both-key) | ✓ |
| Direct-publish states only | Prove states by publishing contracts; rely on SC1 forward round-trip for slot-array coverage, no organic recovery test | |
| You decide the mechanism | Claude's discretion provided each path has a live assertion | |

**User's choice:** Organic recovery + state direct-publish (Recommended)
**Notes:** The roadmap SC-1 requires BOTH the forward pass AND the organic recovery pass (`if exist L2[messageId]` branch); direct-publish alone wouldn't exercise the HGETALL→re-send→retire path organically.

---

## Net-zero scan deltas

| Option | Description | Selected |
|--------|-------------|----------|
| Unfiltered SHA + explicit index check | Unfiltered redis --scan SHA + additive `skp:msg:*` count==0 assertion; drop composite; keep exclusions; no TTL settle | ✓ |
| Unfiltered SHA only | Rely on the --scan SHA alone; drop composite; no separate index count check | |
| You decide | Claude's discretion provided net-zero is active-delete-proven, not TTL settle | |

**User's choice:** Unfiltered SHA + explicit index check (Recommended)
**Notes:** The explicit `skp:msg:*` count==0 makes the A19 active reclaim provable (parallel to the v4 skp-dlq-1 depth==0 check) rather than silently folded into the SHA; the index random TTL (300/600s) cannot be waited out.

---

## Build-before-proof split & operator gate

| Option | Description | Selected |
|--------|-------------|----------|
| Build-first + operator-gated live | Hermetic build gate (0-warning R+D + E2E compile) FIRST; live N=3×GREEN triple-SHA operator-gated via HUMAN-UAT runbook; clone phase-49-close.ps1 | ✓ |
| Build-first + inline live run | Build gate first, then attempt live run inline if the stack is up | |
| Single combined gate | One phase-55-close.ps1 does build + live in one operator-run | |

**User's choice:** Build-first + operator-gated live (Recommended)
**Notes:** Matches the "build-before-proof split" Phase 54 flagged; mirrors Phase 49/39/33 which all operator-gated the live run (needs the rebuilt v5 docker stack; SourceHash must match host build). TEST-01/02 stay unticked until the operator's GREEN run.

---

## Claude's Discretion

- xUnit collection/parallelization shaping for the new organic recovery-pass test.
- Host-Redis polling / ES seam-log assertion mechanics (reuse SC1 precedent).
- Seed-string version value (verify against src/Processor.Sample/appsettings.json).

## Deferred Ideas

None — discussion stayed within the TEST-01/02 scope.
