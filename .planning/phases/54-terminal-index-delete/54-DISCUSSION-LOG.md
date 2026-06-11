# Phase 54: Terminal Index Delete + Atomic Keeper GC - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-12
**Phase:** 54-terminal-index-delete
**Areas discussed:** Tail structure, Key-array shape, Fact placement, Persist-on-escalate failure handling

**Note:** SPEC.md present (GC-01/02/03, ambiguity 0.125) — discussion limited to implementation HOW. WHAT/WHY locked by spec.

---

## Tail structure

| Option | Description | Selected |
|--------|-------------|----------|
| One shared private method | Extract `DeleteTerminalAsync(d, messageId, db, limit, ct)` called by both forward + recovery tails; two-key DEL + persist-escalate + KeeperDelete in one place | ✓ |
| Two inline sites | Keep forward `DeleteSourceTail` and recovery inline tail separate, each repeating the logic | |

**User's choice:** One shared private method
**Notes:** Matches SPEC's "unify the tails" language; least duplication, single home for persist-on-escalate.

---

## Key-array shape

| Option | Description | Selected |
|--------|-------------|----------|
| Inline at call site | `new RedisKey[]{ ExecutionData(entryId), MessageIndex(messageId) }` inline in the RetryLoop call; `L2ProjectionKeys` stays string-only | ✓ |
| Add `TerminalKeys()` helper | New `L2ProjectionKeys.TerminalKeys(entryId, messageId)` returning `RedisKey[]` | |

**User's choice:** Inline at call site
**Notes:** `L2ProjectionKeys` doc already treats key usage as a "caller concern"; the array is StackExchange.Redis-call-local.

---

## Fact placement

| Option | Description | Selected |
|--------|-------------|----------|
| Edit in place, per area | Update inverted facts in `PipelineEndDeleteFacts`; add recovery/REINJECT facts to `PipelineRecoveryFacts`; both-key delete to `DeleteConsumerFacts` | ✓ |
| New grouped A19 fact file | One `TerminalIndexDeleteFacts.cs` for all GC-01/02/03; strip obsolete assertions elsewhere | |

**User's choice:** Edit in place, per area
**Notes:** Two existing facts invert (`EndDelete_RunsOnHappyPath` single→multi-key; `EndDelete_Skipped_OnSourceStep` skip→deletes-index). Each fact stays next to the behavior it tests.

---

## Persist-on-escalate failure handling

| Option | Description | Selected |
|--------|-------------|----------|
| Best-effort — send keeper anyway | If `KeyPersist` exhausts, still send `KeeperDelete`; keeper's atomic both-key DEL is the real GC, index keeps its TTL backstop | ✓ |
| Propagate — throw on persist exhaust | Treat persist-exhaust as a hard fault → broker redelivery, replay re-walks recovery | |

**User's choice:** Best-effort — send keeper anyway
**Notes:** Random TTL retained as A19 crash backstop is exactly why a persist failure can safely fall through to the keeper handoff.

---

## Claude's Discretion

- NSubstitute test-kit mock shape for the multi-key `KeyDeleteAsync(RedisKey[], CommandFlags)` overload.
- Internal control flow of `DeleteTerminalAsync` (param ordering, source-step no-op expression) provided it satisfies the locked decisions.

## Deferred Ideas

None — discussion stayed within phase scope.
