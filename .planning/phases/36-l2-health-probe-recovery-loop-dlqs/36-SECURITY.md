---
phase: 36
slug: l2-health-probe-recovery-loop-dlqs
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-06
---

# Phase 36 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| config → ProbeOptions | appsettings/env `Probe` values bound into the loop bound | operator-controlled config (DelaySeconds, MaxAttempts); internal |
| Redis (L2) ↔ Keeper probe | half-open Redis (read OK, write fails) must count as "down" | probe read + write-then-delete scratch key `skp:keeper:probe:{h}` |
| Keeper → origin endpoint (re-inject) | Keeper `Send`s the verbatim inner message back onto the bus | inner `EntryStepDispatch` / `ExecutionResult` (same `H`); internal bus |
| Keeper → keeper-dlq (park) | Keeper `Send`s the original `Fault<T>` envelope on give-up | `Fault<T>` carrying `Exceptions[]` + original payload; internal bus |
| any consumer → skp-dlq-1 | exhausted (`Immediate(N)`) messages move into one shared forensic queue | original serialized body + content-type + `MT-Fault-*` headers |
| base library → all 3 consoles | a `BaseConsole.Core` change alters error-transport uniformly | applies to processor + orchestrator + Keeper endpoints |
| test → live RabbitMQ/Redis (E2E) | the RealStack E2E poisons a live L2 key + observes real `Fault<T>` traffic | dev-stack only; operator-gated live run |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-36-01 | Tampering | ProbeOptions bound | accept | `ProbeOptions_Bound` test asserts 5×12=60s < 1800s (`ProbeOptionsBoundTests.cs:11-19`; defaults `ProbeOptions.cs:10-11`) | closed |
| T-36-02 | DoS | scratch-key `skp:keeper:probe:{h}` | mitigate | content-addressed by `h`; `StringSetAsync(expiry:30s)` then `KeyDeleteAsync` — net-zero (`L2ProbeRecovery.cs:28-30`, key `L2ProjectionKeys.cs:54`) | closed |
| T-36-03 | Information disclosure | keeper-dlq const name | accept | queue name is not a secret; operators already have broker access (`KeeperQueues.cs:21`) | closed |
| T-36-04 | Spoofing/Tampering | re-injected inner message | accept | verbatim re-inject (same `H`); no Keeper-side dedup; rides receiver Phase-31 `flag[H]` (`FaultEntryStepDispatchConsumer.cs:55-56`) | closed |
| T-36-05 | DoS | half-open Redis swallowed as "up" | mitigate | probe requires BOTH read AND write-then-delete before `Recovered` (`L2ProbeRecovery.cs:27-30`; `Probe_RequiresReadAndWrite` `KeeperProbeLoopTests.cs:53-61`) | closed |
| T-36-06 | DoS | `catch (Exception)` masking a code bug as "down" | mitigate | `catch (RedisException)` ONLY — no `catch (Exception)`; genuine bug propagates → Immediate(N) → DLQ-1 (`L2ProbeRecovery.cs:33`) | closed |
| T-36-07 | Information disclosure | parked `Fault<T>` carries `Exceptions[]`/payload | accept | intake log surfaces only `ExceptionType`+`Message`, no `StackTrace` (`FaultEntryStepDispatchConsumer.cs:42-45`) | closed |
| T-36-08 | DoS | poison-message re-inject loop | accept | pre-existing milestone limitation (Keeper recovers transient L2 faults only); non-L2 fault re-faults → DLQ-1 | closed |
| T-36-09 | DoS | skp-dlq-1 unbounded growth | mitigate | `SetQueueArgument("x-message-ttl", 7d)` = 604800000ms (`MessagingServiceCollectionExtensions.cs:79-82`) | closed |
| T-36-10 | Tampering | error-transport misroute | mitigate | filter targets ONE fixed `skp-dlq-1` const (never config-injected); `GenerateFaultFilter` retained (`ConsolidatedErrorTransportFilter.cs:45-47`, `MessagingServiceCollectionExtensions.cs:60`) | closed |
| T-36-11 | Repudiation | dropped forensic headers on moved message | mitigate | forwards original body + content-type + transport headers + `MT-Fault-*` exception headers (`ConsolidatedErrorTransportFilter.cs:57-75`) | closed |
| T-36-12 | Information disclosure | faulted payloads in skp-dlq-1 | accept | same exposure as existing per-`{queue}_error` default; operators already have broker access | closed |
| T-36-13 | DoS | double-registered retry/error filter (Pitfall 3) | mitigate | `ConfigureError` only inside once-per-endpoint `AddConfigureEndpointsCallback`; sibling `ConfigureConsumer` stays no-op (`MessagingServiceCollectionExtensions.cs:56-63`; `KeeperDlqConsolidationTests.cs:63-71`) | closed |
| T-36-14 | DoS | poison/parked key drifts Phase-39 close-gate triple-SHA | mitigate | net-zero teardown: poison+minted keys in `L2KeysToCleanup`, `skp:keeper:probe:*` self-cleans via TTL, keeper-dlq parked message drained (`KeeperRecoveryE2ETests.cs:144,254-256,720`) — authored, live run operator-gated | closed |
| T-36-15 | Tampering | duplicate downstream effect from re-inject | mitigate | E2E asserts `CountEsHitsAsync == 1` exactly-once via `flag[H]` collapse (`KeeperRecoveryE2ETests.cs:216`) — authored, live run operator-gated | closed |
| T-36-16 | Information disclosure | E2E reads ES logs / broker depth | accept | test-only, dev stack (`[Trait Category=RealStack]`); no new production exposure | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-36-01 | T-36-01 | Probe bound is operator-set internal config; an over-large `DelaySeconds×MaxAttempts` is caught by `ProbeOptions_Bound` (low severity — internal config only) | User | 2026-06-06 |
| AR-36-02 | T-36-03, T-36-12 | Queue names and faulted payloads in DLQs are no new exposure beyond existing `_error` queues — operators already have broker access | User | 2026-06-06 |
| AR-36-03 | T-36-04 | Re-inject is verbatim (same `H`); duplicate collapse is the receiver's surviving Phase-31 `flag[H]` gate (PROBE-06), not a Keeper auth surface | User | 2026-06-06 |
| AR-36-04 | T-36-07 | Parked `Fault<T>` carries `Exceptions[]`/payload but the intake log surfaces only `ExceptionType`+`Message` (no stack frames, T-35-05 kept); drainers already have broker access | User | 2026-06-06 |
| AR-36-05 | T-36-08 | Poison-message re-inject loop is a pre-existing milestone model limitation (Keeper recovers transient L2 faults only); a non-L2 fault re-faults to DLQ-1, not a Phase-36 regression | User | 2026-06-06 |
| AR-36-06 | T-36-16 | E2E reads ES/broker on the dev stack only; test-only, no production exposure | User | 2026-06-06 |

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-06 | 16 | 16 | 0 | gsd-security-auditor (sonnet) — independent source verification |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-06 — 9 mitigations independently confirmed in source (file:line evidence); 7 accepted risks logged. Note: T-36-14/T-36-15 mitigation CODE is verified present in `KeeperRecoveryE2ETests.cs`; their LIVE run is operator-gated (Phase-39 close gate).
