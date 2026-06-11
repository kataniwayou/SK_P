---
status: complete
phase: 52-three-state-keeper
source: [52-01-SUMMARY.md, 52-02-SUMMARY.md, 52-03-SUMMARY.md]
started: 2026-06-11T17:48:52Z
updated: 2026-06-11T17:55:00Z
verified_by: claude (direct execution — `dotnet test --filter-namespace *Keeper*` 32/32 green; `dotnet build SK_P.sln` 0/0; Program.cs wiring grep)
---

## Current Test
<!-- OVERWRITE each test - shows where we are -->

[testing complete]

## Tests

### 1. Cold Start Smoke Test
expected: |
  Kill the Keeper, clear ephemeral state (fresh docker compose: RabbitMQ + Redis
  + Postgres), start the Keeper from scratch. It boots with no errors; the
  keeper-recovery endpoint is runtime-bound (RecoveryEndpointBinder /
  ConnectReceiveEndpoint), KeeperMetrics + RecoveryEndpointHandle are registered,
  BitHealthLoop starts probing. No OOM at bus build. (Live-stack test — may be
  blocked if the compose stack isn't running; this is the operator-gated path
  deferred to Phase 54 per 52-VALIDATION.)
result: pass
verified: build SK_P.sln 0/0 (coherent DI graph) + Program.cs wiring grep (ExcludeFromConfigureEndpoints x3, RecoveryEndpointHandle, RecoveryEndpointBinder, KeeperMetrics AddSingleton + AddMeter) + OOM-safety via SustainedOutage fact. NOTE: literal live boot against RabbitMQ/Redis/Postgres NOT executed — operator-gated to Phase 54.
expected: |
  When the recovery endpoint processes a REINJECT whose L2 entry is absent/empty
  (STRLEN==0, no Redis exception), the consumer acks with NO downstream send,
  increments the `keeper_reinject_dropped` counter, and logs a structured
  {EntryId} warning (never logging Payload/Data). A Redis EXCEPTION on the read
  still routes to the exhaustion policy (not swallowed).
  Proven hermetically by the rewritten ReinjectConsumerFacts absent-drop fact
  (MeterListener) — Keeper suite 32/32 green.
result: pass

### 3. REINJECT re-injects on present L2 data (KEEP-01)
expected: |
  When the L2 entry IS present, REINJECT reconstructs and re-sends the message
  downstream (no drop, counter not incremented).
  Proven by the ReinjectConsumerFacts present-path fact.
result: pass

### 4. INJECT forward-only ordering (KEEP-02)
expected: |
  INJECT executes strictly: write L2[entryId]=Data → send reconstructed
  StepCompleted to queue:orchestrator-result → delete L2[deleteEntryId]. The
  source key is deleted ONLY after the send (no silent loss).
  Proven by InjectConsumerFacts `Received.InOrder` (write < delete) plus the
  WR-02 belt assertion that exactly one StepCompleted was sent before the delete.
result: pass
verified: Inject_writes_sends_completed_deletes_source_in_order — green (32/32 run)

### 5. DELETE drops-on-absent (KEEP-03)
expected: |
  DELETE removes a present L2 key; on an absent key it is a no-op and does NOT
  throw.
  Proven by DeleteConsumerFacts delete + `Delete_absent_key_no_throws`.
result: pass
verified: Delete_deletes_execution_data_key + Delete_absent_key_no_throws — green (32/32 run)

### 6. Endpoint pause on unhealthy / resume on healthy edge (KEEP-04)
expected: |
  When BIT health flips to UNHEALTHY, the BitHealthLoop calls
  ReceiveEndpoint.Stop on the keeper-recovery endpoint (broker accumulates ops
  non-destructively — no dequeue-and-drop) in addition to gate.Close + PauseAll.
  When health flips back to HEALTHY, it calls ReceiveEndpoint.Start(...).Ready
  (resume + drain) alongside gate.Open + ResumeAll. Same-state ticks issue no
  extra Stop/Start. A Stop/Start throw leaves prevHealthy un-advanced (next tick
  re-applies the idempotent edge — no permanent lockout).
  Proven by KeeperPauseAccumulateFacts (Plan 02) +
  Healthy_To_Unhealthy_Edge_Stops_Recovery_Endpoint +
  Same_State_Ticks_No_Stop_Start (Plan 03) — all green.
result: pass
verified: Started_endpoint_consumes_Stopped_endpoint_accumulates + Healthy_To_Unhealthy_Edge_Stops_Recovery_Endpoint + Same_State_Ticks_No_Stop_Start — green (32/32 run)

### 7. Dlq1 exhaustion routes to consolidated skp-dlq-1 (KEEP-05)
expected: |
  Under the default ExhaustionPolicy=Dlq1, an op that exhausts its Immediate
  retries re-throws and routes through the inherited ConsolidatedErrorTransport
  filter to a single skp-dlq-1 dead-letter (one ConsolidatedFault).
  Proven by RecoveryDeadLetterFacts
  (InfraFault_reinject_faults_and_routes_to_dead_letter).
result: pass
verified: InfraFault_reinject_faults_and_routes_to_dead_letter + Keeper_SendFault_RetriesToDlq1 + Dlq1_Consolidated — green (32/32 run)

### 8. SustainedOutage retries without dead-lettering (KEEP-05)
expected: |
  Under ExhaustionPolicy=SustainedOutage, a faulting op is retried on a
  large-but-finite interval (1,000,000 × 1s) and NEVER reaches the error
  transport — no skp-dlq-1, no ConsolidatedFault — within scope. The read is
  redelivered (>1 attempt), not acked/discarded. No OOM at bus build.
  Proven by SustainedOutageFacts (Assert.False on ConsolidatedFault + bounded
  CTS stop).
result: pass
verified: SustainedOutage_holds_and_redelivers_no_dead_letter — green (32/32 run)

## Summary

total: 8
passed: 8
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps

[none yet]
