---
phase: 18-baseconsole-core-library
fixed_at: 2026-05-30T00:00:00Z
review_path: .planning/phases/18-baseconsole-core-library/18-REVIEW.md
iteration: 1
findings_in_scope: 2
fixed: 2
skipped: 0
status: all_fixed
---

# Phase 18: Code Review Fix Report

**Fixed at:** 2026-05-30
**Source review:** .planning/phases/18-baseconsole-core-library/18-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 2 (Critical + Warning; 4 Info findings out of scope, not attempted)
- Fixed: 2
- Skipped: 0

## Fixed Issues

### WR-01: Embedded listener does not dispose the inner WebApplication on stop

**Files modified:** `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs`
**Commit:** d4c0af5
**Applied fix:** Updated `StopAsync` to `await _app.DisposeAsync()` after `StopAsync` and null out `_app`, releasing the inner DI container, Kestrel server, and bound TCP socket instead of leaking them until finalization. Matches the reviewer's suggested fix exactly.

### WR-02: Inner-Kestrel StartAsync has no failure isolation

**Files modified:** `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs`
**Commit:** 4e9e21a
**Applied fix:** Injected `ILogger<EmbeddedHealthEndpointService>` via the constructor (resolved automatically through `AddHostedService<T>`) and wrapped `_app.StartAsync` in a try/catch that logs an actionable error naming the port and the `ConsoleHealth:Port` bind-conflict cause before rethrowing. Per the reviewer's note, the exception is still rethrown (fail-fast with diagnosability) rather than swallowed, preserving existing host-start semantics while making a bind failure diagnosable.

## Skipped Issues

None.

## Out-of-Scope Findings (not attempted)

The following Info findings were outside the `critical_warning` fix scope and were not attempted: IN-01 (FindFreeTcpPort TOCTOU — acknowledged, no change required), IN-02 (Service:Version fail-fast — design preference), IN-03 (BusReadyHealthCheck dual registration — add clarifying comment), IN-04 (correlation filters no-op on non-Guid ambient id — optional doc/log).

## Verification

- `dotnet build src/BaseConsole.Core/BaseConsole.Core.csproj -c Release` -> Build succeeded, 0 Warnings, 0 Errors (after each fix).
- `dotnet build src/BaseConsole.Core/BaseConsole.Core.csproj -c Debug` -> Build succeeded, 0 Warnings, 0 Errors (after each fix).
- `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter FullyQualifiedName~Console` -> Passed: 245, Failed: 0, Skipped: 0 (after each fix). The MTP0001 VSTest-filter note is a pre-existing test-platform warning unrelated to the source changes.

---

_Fixed: 2026-05-30_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
