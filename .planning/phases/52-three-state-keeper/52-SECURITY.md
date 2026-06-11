---
phase: 52
slug: three-state-keeper
status: secured
threats_open: 0
threats_closed: 11
asvs_level: 1
created: 2026-06-11
---

# SECURITY.md — Phase 52: three-state-keeper

**ASVS Level:** 1
**block_on:** high
**Verified:** 2026-06-11
**Result:** SECURED — 11/11 threats closed (8 mitigate verified present, 3 accept rationale-valid)

This file records the disposition-by-disposition verification of the Phase 52 threat
register (T-52-01..T-52-11) against the implemented code. Implementation files were
read-only; no implementation change was made by this audit.

---

## Threat Verification

### Plan 01 — A18 keeper bodies

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-52-01 | Information disclosure | mitigate | CLOSED | `ReinjectConsumer.cs:39` — drop log is `logger.LogWarning("REINJECT drop: L2 data gone EntryId={EntryId}", m.EntryId);`. Structured `{EntryId}` hole only; `m.Payload`/`m.Data` never appear in any log statement (no string interpolation of payload). |
| T-52-02 | Tampering / data-loss correctness | mitigate | CLOSED | `ReinjectConsumer.cs:33-41` — the read runs through `Guard(() => Db.StringLengthAsync(...), ct)`. A Redis EXCEPTION surfaces from `Guard` (RecoveryConsumerBase.cs:48-53 re-throws `outcome.Error`) to the exhaustion policy. The silent drop is gated ONLY on `!= 0` returning false (STRLEN==0, no exception) at line 36 — an exception is never swallowed as a drop. |
| T-52-03 | Tampering / out-of-order effect | mitigate | CLOSED | `InjectConsumer.cs:25-40` — strict write→send→delete: (1) `StringSetAsync` line 25, (2) `GetSendEndpoint` + `ep.Send(completed,...)` lines 36-37, (3) `KeyDeleteAsync(DeleteEntryId)` line 40 is the tail AFTER the confirmed send. `InjectConsumerFacts` locks the order via `Received.InOrder`. |
| T-52-04 | Denial of service (counter leakage) | accept | CLOSED | `KeeperMetrics.cs:26-30` — built via `IMeterFactory` (ctor param `meterFactory.Create(MeterName)`), NOT a static `Meter`. No cross-test process leakage; internal consumer, no external DoS surface. Accept rationale holds. |

### Plan 02 — endpoint pause/resume + exhaustion policy

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-52-05 | Tampering / duplicate-endpoint | mitigate | CLOSED | `Program.cs:53-55` — all three recovery consumers registered `.ExcludeFromConfigureEndpoints()`. The ONLY endpoint config source is `RecoveryEndpointBinder.cs:80` `connector.ConnectReceiveEndpoint(KeeperQueues.Recovery, ...)`. No static `AddConsumer(def)` auto-config remains; the two no-op sibling definitions were deleted. No static+connect collision. |
| T-52-06 | Repudiation / data-loss (SustainedOutage) | mitigate | CLOSED | `RecoveryEndpointBinder.cs:86-93` — SustainedOutage wires `UseMessageRetry(r => r.Interval(SustainedOutageRetryCount, SustainedOutageInterval))` with a large-but-finite count (`1_000_000`, line 66), NOT `int.MaxValue` (which OOMs the pre-allocated `TimeSpan[]`). A thrown delivery is redelivered in-process, never acked/discarded. `SustainedOutageFacts` proves no ConsolidatedFault + read retried >1. |
| T-52-07 | Elevation / dead-letter integrity | mitigate | CLOSED | `RecoveryEndpointBinder.cs:80-108` — NO per-consumer `cfg.ConfigureError(...)` call anywhere in the binder callback; the consolidated `skp-dlq-1` filter is inherited from BaseConsole.Core. (`ConfigureError` appears only in xml-doc prose explaining its deliberate absence.) Dlq1 branch (line 98) re-throws → single inherited ConsolidatedFault. |
| T-52-08 | DoS / startup race | accept | CLOSED | `RecoveryEndpointBinder.cs:38-45` (xml-doc) + connect STARTED at line 80. Consumption only mutates L2 on op success; first BIT tick fires within `Probe:DelaySeconds` (appsettings.json:30 = 5s). Brief pre-probe consumable window documented as accepted residual. Accept rationale holds. |

### Plan 03 — BitHealthLoop Stop/Start driver

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-52-09 | Elevation / gate-bypass | mitigate | CLOSED | `BitHealthLoop.cs:62-66` — the unhealthy edge calls `gate.Close()` then `if (endpointHandle.Handle is { } h) await h.ReceiveEndpoint.Stop(stoppingToken);` BEFORE `prevHealthy = healthy;` (line 70). Consumption is paused at the transport so no L2 op runs while the gate is closed. `Healthy_To_Unhealthy_Edge_Stops_Recovery_Endpoint` asserts the Stop call. |
| T-52-10 | DoS / stuck gate (WR-01) | mitigate | CLOSED | `BitHealthLoop.cs:46-80` — both Start (line 56, `await ...Start(stoppingToken).Ready`) and Stop (line 66) await inside the existing WR-01 `try`. A throw lands in `catch (Exception ex)` (line 73-80); `prevHealthy` is NOT advanced (assignment at line 70 is skipped), so the next tick re-applies the idempotent edge. Loop is never permanently killed. |
| T-52-11 | Tampering / startup window | accept | CLOSED | `BitHealthLoop.cs:55,65` — null-guarded via `endpointHandle.Handle is { } h`; never throws on a null handle. `RecoveryEndpointHandle.cs:28` backs the handle with a `volatile` field so the binder's set is promptly visible to the loop thread, bounding the pre-bind window. Next tick (within Probe:DelaySeconds) applies the edge once present. Accept rationale holds. |

---

## Accepted Risks Log

| Threat ID | Accepted Residual | Justification |
|-----------|-------------------|---------------|
| T-52-04 | Counter instrument is process-internal | IMeterFactory (not static Meter) prevents cross-test leakage; no external DoS surface on an internal recovery consumer. |
| T-52-08 | keeper-recovery endpoint briefly consumable before the first BIT probe | Connect-started posture (matches processor analog). Consumption only mutates L2 on op success; first BIT tick fires within Probe:DelaySeconds (5s). Documented in RecoveryEndpointBinder xml-doc. |
| T-52-11 | Null handle in the pre-bind startup window | Null-guarded (`is { } h`), volatile-backed for prompt visibility; next tick re-applies the edge once the binder sets the handle. Same accepted class as T-52-08. |

---

## Unregistered Flags

None. All three plan SUMMARY files (`52-01`, `52-02`, `52-03`) report `## Threat Flags: None` —
no new attack surface was detected during implementation beyond the declared register. The
code-review-fix pass noted in the audit constraints (RecoveryEndpointHandle.Handle made
`volatile` for WR-01, GetSendEndpoint wrapped in Guard for IN-01) is reflected in the code and
strengthens T-52-02 (Guard coverage of the read+send) and T-52-11 (volatile visibility).
