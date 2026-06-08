---
phase: 45
slug: keeper-bit-health-gate-global-pause-resume
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-08
---

# Phase 45 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| Keeper BIT loop → orchestrator replicas (MassTransit bus) | Internal control-plane only; no external publisher. Edge-triggered `PauseAll`/`ResumeAll` broadcast. | A single tracing `Guid CorrelationId` — no free-text, no payload. |
| Redis (L2) → Keeper BIT probe | Untrusted infra availability; a `RedisException` is the ONLY signal that maps to "unhealthy". | Probe read + write-then-delete sentinel. |
| Bus (`PauseAll`/`ResumeAll`) → orchestrator replica's in-memory Quartz scheduler + L1 | Internal control-plane; each replica acts on its own per-instance state. | `Guid CorrelationId` only. |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-45-01 | Tampering | PauseAll/ResumeAll log fields | mitigate | Contracts carry only a `Guid CorrelationId`; consumers log via structured template holes, never interpolation. Verified: `PauseAll.cs:6`, `ResumeAll.cs:6`. | closed |
| T-45-02 | Information Disclosure | contract payload | accept | No payload beyond a tracing `Guid`; nothing sensitive on the wire (no `string H` / `WorkflowId`). | closed |
| T-45-03 | Denial of Service | ProbeOnceAsync bug → false "L2 down" → fleet-wide false PauseAll storm | mitigate | `ProbeOnceAsync` catches `RedisException` ONLY (`L2ProbeRecovery.cs:67`, no `catch (Exception)`); the probe call at `BitHealthLoop.cs:32` is outside any try/catch so a non-Redis bug propagates out of `ExecuteAsync`. | closed |
| T-45-04 | Denial of Service | BIT loop per-tick broadcast spam | mitigate | Edge-trigger `if (prevHealthy != healthy)` (`BitHealthLoop.cs:34`); steadily-healthy emits zero traffic. | closed |
| T-45-05 | Information Disclosure | probe/transition logs leaking Redis internals | mitigate | Transition logs are constant strings (`BitHealthLoop.cs:42,48`); publish-failure log (line 59) carries only the exception object, no connection internals; `ProbeOnceAsync` logs nothing on `RedisException`. | closed |
| T-45-06 | Tampering | log injection via interpolated message bodies | mitigate | No `$"..."` interpolation in `BitHealthLoop.cs` (0 matches); string-literal templates only. | closed |
| T-45-07 | Denial of Service | native `ResumeAll()` catch-up herd | mitigate | `ResumeAllConsumer` uses per-job `lifecycle.ResumeAsync` only (`ResumeAllConsumer.cs:32–33`); native `scheduler.ResumeAll()` is never invoked (sole match is a doc-comment warning). | closed |
| T-45-08 | Denial of Service | double-wrapped retry on the shared endpoint | mitigate | Retry registered ONCE — only `PauseAllConsumerDefinition.cs:37` owns `UseMessageRetry`; the Resume def sets only `ConcurrentMessageLimit = 1`. | closed |
| T-45-09 | Tampering | log injection via interpolated control-message bodies | mitigate | Both consumers log `{CorrelationId}` via structured template holes only (`PauseAllConsumer.cs:23`, `ResumeAllConsumer.cs:31`); 0 `$"` matches; only a `Guid` traverses. | closed |
| T-45-10 | Spoofing | spoofed PauseAll flooding the fleet | accept | Internal-only bus (no external publisher); idempotent consumers absorb redelivery; edge-trigger upstream limits legitimate volume. Out of scope to harden further this phase. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-45-01 | T-45-02 | The `PauseAll`/`ResumeAll` contracts carry only a tracing `Guid CorrelationId` — there is no sensitive data on the wire and no validation surface beyond a GUID. | User | 2026-06-08 |
| AR-45-02 | T-45-10 | The pause/resume bus is an internal control-plane with no external publisher; consumers are idempotent (Quartz no-op on re-pause/`Paused`-guarded resume) and the upstream edge-trigger bounds legitimate volume. Authenticating internal publishers is out of scope for this phase. | User | 2026-06-08 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-08 | 10 | 10 | 0 | gsd-security-auditor (verify-all, code-evidence) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-08
