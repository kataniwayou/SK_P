---
status: partial
phase: 08-entity-build-out-migrations-docker-runtime-tests
source: [08-VERIFICATION.md]
started: 2026-05-28T00:00:00Z
updated: 2026-05-28T00:00:00Z
---

## Current Test

[awaiting human testing]

## Tests

### 1. Full Suite 3-Consecutive GREEN Runs
expected: `dotnet test SK_P.sln` with Docker Compose stack live → 3 consecutive runs each show 128 Passed / 0 Failed (98 Phase 1-7 + 25 Wave B + 4 ErrorMapping + 1 MigrationFailure). OTel warmup flakes may require extra runs.
result: [pending]

### 2. Byte-Identical psql \l Snapshot
expected: BEFORE/AFTER `psql -l` snapshots produce empty diff and matching SHA-256 (expected hash `0d98b0de57125b164489958eef5fc3da26969d18a7ef8bba845da02f20aac127`). Only 4 baseline databases visible — no leaked stepsdb_test_<guid> databases.
result: [pending]

### 3. Docker Compose End-to-End Boot (SC#1 + SC#6)
expected: `docker compose up --build -d` + 30s wait → `curl http://localhost:8080/health/live` returns 200; `curl http://localhost:8080/api/v1/schemas` returns 200 with body `[]`. `docker compose down -v` cleans up without errors.
result: [pending]

## Summary

total: 3
passed: 0
issues: 0
pending: 3
skipped: 0
blocked: 0

## Gaps
