# Phase 24 — Deferred / Tracking Items

## SCOPE-BOUNDARY: post-24-02 full-suite "4 failures" — UNDER FINAL VERIFICATION

**Status:** being settled at phase close (NOT a confirmed false alarm — earlier "RESOLVED"
note was based on a misread of a wrapper exit code and has been retracted).

**Timeline of evidence:**
- After 24-02, orchestrator's own from-scratch `dotnet test SK_P.sln -c Debug` exited **1**
  with `Failed: 4, Passed: 307, Total: 311`. (The background-task notification said "exit code 0"
  — that is the wrapper's code, not dotnet's; the captured log shows `EXIT:1`.)
- 24-03 executor characterized the 4 as pre-existing RabbitMQ / DbConcurrency / FluentValidation
  integration flakies outside the Orchestrator namespace.
- 24-05 executor reported a from-scratch rebuild (`rm -rf */obj */bin`) at **309 passed / 0 failed**,
  and separately found a real stale-build trap (AckSemanticsTests compiled against a removed ctor,
  masked by incremental build) — consistent with the project's known stale-build memory.

**Resolution path:** orchestrator runs one authoritative `dotnet clean` + full-suite run with a
TRX report at phase close; if any test fails, the exact failing names are read from the TRX and
classified (24-caused vs pre-existing flaky) before the phase is marked verified. Result recorded
in 24-VERIFICATION.md.
