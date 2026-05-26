---
phase: 01-repository-scaffold
plan: 03
subsystem: infra
tags: [verification, dotnet-build, dotnet-test, xunit-v3, microsoft-testing-platform, boot-smoke, phase-1-acceptance]

# Dependency graph
requires:
  - "01-01: global.json (SDK pin), Directory.Build.props (TreatWarningsAsErrors), Directory.Packages.props (CPM with corrected xunit.runner.visualstudio 3.1.5), .editorconfig, .gitignore, .gitattributes, README.md"
  - "01-02: SK_P.sln, three csproj files (with the four MTP/CPM scaffold fixes layered on BaseApi.Tests.csproj), folder skeleton, Program.cs (D-10), appsettings.json (REQ INFRA-04), MetaTest.cs (D-11)"
provides:
  - "Phase 1 SC#1 GREEN: zero-warning build verified across both Release and Debug configurations (aggregate evaluation per W-02)"
  - "Phase 1 SC#2 GREEN: dotnet --version returns the literal pinned 8.0.421 at repo root (rollForward did not kick in — 8.0.421 is exactly installed)"
  - "Phase 1 SC#3 GREEN: CPM resolution confirmed via dotnet list package — all three xunit.v3 references resolve to Directory.Packages.props pins"
  - "Phase 1 SC#4 GREEN: appsettings.json valid JSON with all 4 REQ INFRA-04 sections and locked Service.Name/Version values"
  - "REQ TEST-01 GREEN: dotnet test routes through Microsoft.Testing.Platform; MetaTest.Sanity passes (1/1)"
  - "D-10 scaffold GREEN: BaseApi.Service boots within 30s, all 5 probe paths return HTTP 404, process terminates cleanly with no orphan dotnet processes"
  - "Four scaffold gaps surfaced + fixed by Plan 01-03's verification gate (pin correction + three MTP setup properties) — xunit.v3 3.2.2 + SDK 8 MTP integration is now canonically wired"
affects: [02-postgres-compose, 03-ef-core-base, 04-middleware, 05-observability, 06-validation-mapping, 07-http-base, 08-entities]

# Tech tracking
tech-stack:
  added: []   # Plan 03 writes no source files; only the four scaffold-fix commits modify code (csproj + Directory.Packages.props)
  patterns:
    - "Verification-as-gate: Plan 01-03's <automated> verify blocks surfaced four real scaffold defects that pure file-existence checks in Plans 01 + 02 would have missed (pin existence, exe routing, MTP runner routing, dotnet test routing)"
    - "MTP scaffold canon: <OutputType>Exe</OutputType> + <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner> + <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport> are required together for xunit.v3 3.2.2 + SDK 8 dotnet test integration"
    - "Pin-existence validation: NuGet pins must resolve at restore time, not just exist in the central props file (xunit.runner.visualstudio 3.1.7 was a hallucinated version; latest stable on the v3-compatible line is 3.1.5)"

key-files:
  created:
    - .planning/phases/01-repository-scaffold/01-03-SUMMARY.md
  modified:
    - Directory.Packages.props                  # d106972 (Plan 01-01 deviation — xunit.runner.visualstudio 3.1.7 -> 3.1.5)
    - tests/BaseApi.Tests/BaseApi.Tests.csproj  # 630b6ee + e60d001 + ea02ad9 (Plan 01-02 deviations — OutputType=Exe + UseMicrosoftTestingPlatformRunner + TestingPlatformDotnetTestSupport)
    - .planning/phases/01-repository-scaffold/01-03-PLAN.md  # 72ba670 (plan acceptance criteria reference 3.1.5 instead of 3.1.7)

key-decisions:
  - "Plan 03 surfaced four scaffold defects via its verification gate — all auto-fixed under Rule 3 (Blocking, unblocks current task) rather than escalated under Rule 4 (no schema changes, no library swaps, no architectural shifts)"
  - "Pin correction (d106972): xunit.runner.visualstudio 3.1.7 does not exist on NuGet.org; latest stable line compatible with xunit.v3 3.2.2 is 3.1.5 — pin amended in Directory.Packages.props"
  - "MTP scaffold (630b6ee + e60d001 + ea02ad9): three csproj properties are required for the xunit.v3 3.2.2 + SDK 8 dotnet test path — OutputType=Exe (runtime-host), UseMicrosoftTestingPlatformRunner=true (executable opts into MTP), TestingPlatformDotnetTestSupport=true (SDK routes dotnet test to MTP entry-point instead of legacy VSTest); the gap was discovered iteratively because each property's absence is reported with a different (sometimes misleading) error"
  - "rollForward did not activate: 8.0.421 SDK is installed exactly; dotnet --version returned the literal pinned value, not a higher 8.0.x patch — no W-05 substitution needed"
  - "Boot smoke succeeded on the first poll iteration (< 1 second) — Program.cs (D-10 scaffold) + appsettings.json (REQ INFRA-04) compose correctly; 5/5 paths return 404 as expected for Phase 1 with no routes registered"

patterns-established:
  - "Verification-gate scaffold surfacing: a phase's verification plan can legitimately uncover defects in earlier plans of the same phase — when discovered, they are documented as deviations on whichever plan the defect originally lived in (01-01 for the pin, 01-02 for the MTP properties), with the fix commit attributed to the discovering plan (01-03) in this SUMMARY"
  - "Layered fix-forward: when a multi-defect surface requires repeated diagnose-fix-rerun cycles, each fix lands as its own atomic commit so the history captures the iterative discovery (5 commits total across Plan 03's verification cycle)"

requirements-completed: [INFRA-01, INFRA-02, INFRA-03, INFRA-04]

# Metrics
duration: ~75min wall-clock (multi-checkpoint resumes; net iterative-diagnosis work concentrated in 4 scaffold-fix cycles)
completed: 2026-05-26
---

# Phase 01 Plan 03: Acceptance Verification Summary

**Plan 03 ran the full Phase 1 acceptance battery (dotnet --version, dotnet restore, dotnet build Release+Debug, dotnet test, dotnet run boot smoke) and surfaced four real scaffold defects in the process — all auto-fixed under deviation Rule 3 (Blocking) without re-opening any architectural decisions. All four ROADMAP success criteria are now GREEN; the four INFRA-* requirements are closed; Phase 1 ships.**

**Plan:** 01-repository-scaffold / 01-03
**Executed:** 2026-05-26T18:33:53Z (final pass)
**Result:** GREEN — Phase 1 complete, all 4 ROADMAP success criteria verified
**Resolved SDK:** `8.0.421` (literal pinned value; rollForward did not activate)

## Performance

- **Duration:** ~75 min wall-clock across four diagnose-fix-resume cycles
- **Started:** 2026-05-26 (post-Plan 02 close)
- **Completed:** 2026-05-26T18:33:53Z
- **Tasks:** 4 plan tasks (Task 1 SDK + appsettings; Task 2 build + CPM; Task 3a test; Task 3b boot smoke; Task 4 SUMMARY)
- **Files modified by this plan's verification cycle:**
  - `Directory.Packages.props` (d106972 — pin correction)
  - `tests/BaseApi.Tests/BaseApi.Tests.csproj` (630b6ee + e60d001 + ea02ad9 — three MTP scaffold properties)
  - `.planning/phases/01-repository-scaffold/01-03-PLAN.md` (72ba670 — pin reference text update)
  - `.planning/phases/01-repository-scaffold/01-03-SUMMARY.md` (this file)

## Phase 1 Success Criteria — Verification (verbatim evidence)

### SC#1: dotnet build at repo root succeeds with zero warnings (BOTH configurations, aggregated — W-02)

**Command (Release):** `dotnet build --configuration Release`
**Verbatim tail (post-MTP-routing fix):**

```
  Determining projects to restore...
  All projects are up-to-date for restore.
  BaseApi.Core -> C:\Users\UserL\source\repos\SK_P\src\BaseApi.Core\bin\Release\net8.0\BaseApi.Core.dll
  BaseApi.Tests -> C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\bin\Release\net8.0\BaseApi.Tests.dll
  BaseApi.Service -> C:\Users\UserL\source\repos\SK_P\src\BaseApi.Service\bin\Release\net8.0\BaseApi.Service.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:00.89
```

**Command (Debug):** `dotnet build --configuration Debug`
**Verbatim tail:**

```
  Determining projects to restore...
  All projects are up-to-date for restore.
  BaseApi.Core -> C:\Users\UserL\source\repos\SK_P\src\BaseApi.Core\bin\Debug\net8.0\BaseApi.Core.dll
  BaseApi.Tests -> C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\bin\Debug\net8.0\BaseApi.Tests.dll
  BaseApi.Service -> C:\Users\UserL\source\repos\SK_P\src\BaseApi.Service\bin\Debug\net8.0\BaseApi.Service.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:01.09
```

**Aggregate evaluation (per W-02):** Debug ran UNCONDITIONALLY (not gated on Release passing). Both exits = 0, both outputs reported `0 Warning(s)` and `0 Error(s)`. Aggregate: GREEN.

`TreatWarningsAsErrors=true` (Directory.Build.props from Plan 01) means the build would have failed if any warning existed — `Build succeeded` is itself proof of zero warnings.

### SC#2: dotnet --version returns the pinned SDK

**Command:** `dotnet --version` (at repo root)
**Verbatim output:** `8.0.421`
**Pinned in:** `global.json` (Plan 01 Task 1) — SDK="8.0.421", rollForward="latestFeature"
**Available SDKs (`dotnet --list-sdks`):**

```
8.0.421 [C:\Program Files\dotnet\sdk]
9.0.100 [C:\Program Files\dotnet\sdk]
```

`rollForward=latestFeature` did NOT activate — the exact pinned SDK is installed and used. The 9.0.100 SDK on disk does not float because global.json constrains to the 8.x band.

GREEN.

### SC#3: NuGet references resolve from Directory.Packages.props (no per-project Version attributes)

**Command:** `dotnet list tests/BaseApi.Tests/BaseApi.Tests.csproj package`
**Verbatim output:**

```
Project 'BaseApi.Tests' has the following package references
   [net8.0]:
   Top-level Package                Requested   Resolved
   > xunit.runner.visualstudio      3.1.5       3.1.5
   > xunit.v3                       3.2.2       3.2.2
   > xunit.v3.assert                3.2.2       3.2.2
```

**Resolved versions match `Directory.Packages.props` exactly:**
- `xunit.v3` -> 3.2.2 (pinned 3.2.2)
- `xunit.v3.assert` -> 3.2.2 (pinned 3.2.2)
- `xunit.runner.visualstudio` -> 3.1.5 (pinned 3.1.5 — corrected from non-existent 3.1.7 in d106972)

**csproj inspection:** BaseApi.Core.csproj, BaseApi.Service.csproj, BaseApi.Tests.csproj all carry ZERO `Version=` attributes on any `<PackageReference>` element. CPM resolution is the only version source.

GREEN.

### SC#4: appsettings.json contains all 4 sections, valid JSON, no comments

**File:** `src/BaseApi.Service/appsettings.json`
**JSON parse:** PASS (`ConvertFrom-Json` succeeded)
**Sections present:** `Logging`, `Service`, `ConnectionStrings`, `OpenTelemetry` (all 4 REQ INFRA-04 sections)
**Locked values:** `Service.Name="steps-api"`, `Service.Version="3.2.0"`
**ConnectionStrings.Postgres:** `Host=postgres;Port=5432;Database=stepsdb;Username=postgres;Password=postgres;Maximum Pool Size=20;Timeout=15`
**Comment scan:** No line begins with `//` or `/*` (after optional whitespace). The string `//` does appear as a substring inside the JSON string value `http://otel-collector:4317` — this is a URL substring inside a double-quoted JSON string, NOT a JSON-syntactic comment. The default `System.Text.Json` parser (which `ConvertFrom-Json` wraps) successfully parses the file, which is operational proof that no JSON-syntactic comments exist. This is the deviation pattern documented in Plan 01-02 SUMMARY (verifier regex over-broadness on URL substrings).

**Companion file:** `src/BaseApi.Service/appsettings.Development.json` parses cleanly via `ConvertFrom-Json`.

GREEN.

## Phase 1 Behavior Smoke Tests

### Test stack (D-11 + REQ TEST-01) — Task 3a automated gate

**Command:** `dotnet test --no-build --configuration Release`
**Verbatim output (after MTP routing fix landed in ea02ad9):**

```
  Run tests: 'C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\bin\Release\net8.0\BaseApi.Tests.dll' [net8.0|x64]
  Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 171ms - BaseApi.Tests.dll (net8.0|x64)
  Tests succeeded: 'C:\Users\UserL\source\repos\SK_P\tests\BaseApi.Tests\bin\Release\net8.0\BaseApi.Tests.dll' [net8.0|x64]
```

**Routing evidence:** The output header reads `Run tests: '...BaseApi.Tests.dll' [net8.0|x64]` — this is the Microsoft.Testing.Platform (MTP) idiom. Before the four scaffold fixes, this header read `VSTest version 17.11.1 (x64)` indicating the legacy VSTest host was being invoked. The new header confirms `dotnet test` now routes through MTP via `TestingPlatformDotnetTestSupport=true` + invokes the MTP runner via `UseMicrosoftTestingPlatformRunner=true` + targets the exe produced by `OutputType=Exe` — all three csproj properties are required for the routing.

**Result:** Passed: 1, Failed: 0, Skipped: 0, Total: 1, Duration: 171ms
**Test name:** `BaseApi.Tests.MetaTest.Sanity` (the [Fact] declared in Plan 02 Task 6 — D-11)
**Exit code:** 0

GREEN — xUnit v3 + xunit.runner.visualstudio 3.1.5 + Microsoft.Testing.Platform wired correctly. REQ TEST-01 verified.

### Service boot smoke (D-10 scaffold) — Task 3b automated gate (per B-01 fix)

**Command:** `dotnet run --project src/BaseApi.Service --no-build --configuration Release --urls http://127.0.0.1:5099` (background)
**Startup observation:** Host bound at iteration 0 (under 1 second) — `Now listening on: http://127.0.0.1:5099` appeared in stdout almost immediately.

**Verbatim host stdout (first 6 lines, captured to `$env:TEMP\baseapi-service-boot.log`):**

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://127.0.0.1:5099
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Production
info: Microsoft.Hosting.Lifetime[0]
      Content root path: C:\Users\UserL\source\repos\SK_P\src\BaseApi.Service
```

**stderr capture:** Empty — host emitted no errors during the run.

**Probe results (verbatim, all returned HTTP 404 — expected for Phase 1 with no routes mapped):**

```
READY at iteration 0: GET / -> 404
PASS: GET / -> 404
PASS: GET /api/foo -> 404
PASS: GET /health/live -> 404
PASS: GET /random/path -> 404
PASS: GET /favicon.ico -> 404
```

**Process cleanup:**
- `Stop-Process -Id 18804 -Force` — process terminated cleanly
- Post-cleanup `Get-Process -Name dotnet` returned 3 SDK daemons (VBCSCompiler + 2 MSBuild node-reuse workers); zero of them reference `BaseApi.Service`. No orphans from the boot smoke.

GREEN — Host boots, appsettings.json loads (otherwise the host would crash on bind), all 5 probe paths return 404 (proving D-10 AddControllers + MapControllers scaffold is functional with no routes registered). CI-grade automated verify enforces D-10 contract — B-01 closed.

## Phase 1 Requirements Closed

| ID | Requirement | Closed by |
|----|-------------|-----------|
| INFRA-01 | Solution structure `src/BaseApi.Core/` + `src/BaseApi.Service/` + `tests/BaseApi.Tests/` | Plan 02 Task 1 (csproj files) + Plan 03 SC#1 verification (build succeeds across all 3 projects) |
| INFRA-02 | .NET 8.0 SDK pinned via global.json | Plan 01 Task 1 + Plan 03 SC#2 verification (`dotnet --version` returns 8.0.421) |
| INFRA-03 | Directory.Packages.props centralizes NuGet versions | Plan 01 Task 3 + Plan 03 SC#3 verification (`dotnet list package` shows CPM resolution) |
| INFRA-04 | appsettings.json contains Logging, Service, ConnectionStrings:Postgres, OpenTelemetry | Plan 02 Task 5 + Plan 03 SC#4 verification (ConvertFrom-Json parse + section + locked-value check) |

## Files Created (Phase 1 totals across Plans 01-03)

**Plan 01 (repo root):** 7 files
**Plan 02 (solution + projects + scaffold):** 23 files (1 sln + 3 csproj + 1 Program.cs + 2 appsettings + 1 MetaTest.cs + 15 .gitkeep)
**Plan 03 (verification + this SUMMARY + four scaffold-fix amendments to earlier plans' files):** 1 file written (this SUMMARY); 0 new source files created; 3 existing files amended (Directory.Packages.props, BaseApi.Tests.csproj, 01-03-PLAN.md text)

**Total new files in Phase 1:** 31 (7 + 23 + 1).

## Decisions Made

None of consequence — Plan executed against the four ROADMAP success criteria as specified. The four scaffold defects surfaced by the verification cycle were all auto-resolved under deviation Rule 3 (Blocking — unblocks current task) without re-opening architectural decisions. No library was swapped, no schema was added, no host topology was changed. The xunit.v3 + MTP wiring is now the canonical Microsoft-documented setup; the corrected pin is the latest stable on its line.

## Deviations from Plan

This plan's verification cycle uncovered four scaffold defects that Plans 01 + 02 file-existence verifiers could not catch — only the actual `dotnet restore` / `dotnet test` invocations surfaced them. All four are attributed to whichever earlier plan originally introduced the gap; commits land on master with the discovering plan (01-03) noted in the messages where useful.

### Auto-fixed Issues

**1. [Rule 3 — Blocking] Hallucinated NuGet pin: xunit.runner.visualstudio 3.1.7 does not exist on NuGet.org**

- **Found during:** Plan 03 Task 2 (`dotnet restore --force --no-cache`)
- **Issue:** `Directory.Packages.props` pinned `xunit.runner.visualstudio` to `3.1.7`. Restore failed with `NU1101: Unable to find package 'xunit.runner.visualstudio' with version (= 3.1.7)`. NuGet.org confirms 3.1.7 was never published on the v3-compatible line; the latest stable line compatible with xunit.v3 3.2.2 is 3.1.5.
- **Root cause:** Plan 01-01's NuGet pin table sourced the version from STACK.md, which appears to have been authored against assumed-future versions rather than the live nuget.org catalog at the time of Phase 1 execution.
- **Fix:** Amended `Directory.Packages.props` line to `<PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />`. Restore then succeeded; `dotnet list package` confirms resolution at 3.1.5.
- **Files modified:** `Directory.Packages.props` (1 line)
- **Attribution:** Plan 01-01 deviation (pin authoring); discovered + fixed during Plan 01-03's restore step.
- **Commit:** `d106972` — `fix(01-01): correct xunit.runner.visualstudio pin from non-existent 3.1.7 to 3.1.5`
- **Companion text update:** `72ba670` — `docs(01-03): update Task 2 acceptance criteria to reference corrected pin` (kept the plan body in sync with the corrected pin so re-readers of the plan see the right number).

**2. [Rule 3 — Blocking] BaseApi.Tests.csproj missing `<OutputType>Exe</OutputType>` — required by xunit.v3 3.2.2's Microsoft.Testing.Platform integration**

- **Found during:** Plan 03 Task 2 (`dotnet build --configuration Release`)
- **Issue:** First post-restore build emitted: `MSB4030: error : test projects must be executable (set project property <OutputType>Exe</OutputType>)`. xunit.v3 3.2.2 ships under Microsoft.Testing.Platform (MTP), which mandates that test projects produce an executable host (an .exe that hosts the MTP runner), not a library DLL.
- **Root cause:** Plan 01-02's BaseApi.Tests.csproj template followed the xunit.v2 / VSTest convention where test projects were libraries. xunit.v3 changed this; the CONTEXT.md research authored before xunit.v3 3.2.2 was the active stable line did not surface the MTP exe requirement.
- **Fix:** Added `<OutputType>Exe</OutputType>` to `BaseApi.Tests.csproj`'s PropertyGroup with an inline comment explaining the MTP requirement.
- **Files modified:** `tests/BaseApi.Tests/BaseApi.Tests.csproj`
- **Attribution:** Plan 01-02 deviation (csproj scaffold); discovered + fixed during Plan 01-03's first build attempt.
- **Commit:** `630b6ee` — `fix(01-02): add <OutputType>Exe</OutputType> required by xunit.v3 3.2.2 MTP integration`

**3. [Rule 3 — Blocking] BaseApi.Tests.csproj missing `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` — runs MTP runner inside the exe**

- **Found during:** Plan 03 Task 3a (`dotnet test --no-build`)
- **Issue:** After adding OutputType=Exe, the build succeeded but `dotnet test` failed at runtime trying to resolve Newtonsoft.Json 13.0.1 via the VSTest testhost manifest (which is not on disk for an xunit.v3 stack). The exe was built but its entry point still went through the legacy VSTest test-runner machinery instead of the MTP runner.
- **Root cause:** Same as #2 — the csproj template did not include the modern MTP runner-selection property. xunit.v3 + MTP require this property to opt the executable into invoking the MTP runner rather than VSTest.
- **Fix:** Added `<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>` with an inline comment explaining the MTP routing.
- **Files modified:** `tests/BaseApi.Tests/BaseApi.Tests.csproj`
- **Attribution:** Plan 01-02 deviation; discovered + fixed during Plan 01-03's test invocation.
- **Commit:** `e60d001` — `fix(01-02): opt BaseApi.Tests into Microsoft.Testing.Platform runner for dotnet test`

**4. [Rule 3 — Blocking] BaseApi.Tests.csproj missing `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` — `dotnet test` SDK-level routing**

- **Found during:** Plan 03 Task 3a (post-deviation #3 re-run of `dotnet test`)
- **Issue:** After adding UseMicrosoftTestingPlatformRunner=true, the test exe ran fine when invoked directly (`BaseApi.Tests.exe --help` printed the canonical MTP runner help banner), but `dotnet test` still routed through the legacy VSTest host. Output header read `VSTest version 17.11.1` instead of the MTP idiom `Run tests: '...dll' [net8.0|x64]`.
- **Root cause:** On SDK 8/9, `dotnet test`'s default routing is VSTest. `UseMicrosoftTestingPlatformRunner` only controls what the test executable does *once started*; it doesn't change how `dotnet test` chooses which entry-point to start. Per the xunit.net + Microsoft.Testing.Platform documentation, the SDK-level routing requires the companion property `TestingPlatformDotnetTestSupport=true` to switch `dotnet test`'s invocation path.
- **Fix:** Added `<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>` adjacent to UseMicrosoftTestingPlatformRunner in the PropertyGroup, with an inline comment explaining the SDK-level routing distinction. This was the fourth and final scaffold gap; after the commit, `dotnet test` output header switched to MTP idiom and MetaTest.Sanity was discovered + passed (1/1).
- **Files modified:** `tests/BaseApi.Tests/BaseApi.Tests.csproj`
- **Attribution:** Plan 01-02 deviation; discovered + fixed during Plan 01-03's test invocation; required user approval at a checkpoint because three prior fix-forward cycles in the same task had already landed and the deviation framework's "3-attempts" guard had been reached.
- **Commit:** `ea02ad9` — `fix(01-02): route dotnet test through MTP via TestingPlatformDotnetTestSupport`

### Architectural changes

None. All four fixes are scaffold corrections — they wire up properties / pins that the canonical xunit.v3 3.2.2 + SDK 8 documentation specifies but Plans 01 + 02 omitted. No new dependencies, no schema, no host topology changes.

### Total deviations: 4 auto-fixed (all Rule 3 — Blocking)

**Impact on plan:** All four ROADMAP success criteria for Phase 1 are GREEN. The xunit.v3 3.2.2 + SDK 8 MTP integration is now canonically configured for all future test additions in Phases 2-8 — no further scaffold work is required for the test project. The pin correction in Directory.Packages.props prevents the NU1101 restore failure from re-appearing on any future machine. No re-planning, no architectural rework, no requirements removed.

## Authentication / Human-Action Gates Encountered

One human-action gate occurred during the verification cycle: after three consecutive scaffold fixes (pin, OutputType, UseMicrosoftTestingPlatformRunner) the deviation framework's 3-attempts guard surfaced a checkpoint to request user approval before applying the fourth fix (TestingPlatformDotnetTestSupport). The user approved Option 1 (add the property in place) and execution resumed.

## Issues Encountered

The verification cycle ran across four diagnose-fix-rerun iterations. Each iteration produced one atomic commit. No work was discarded; every fix builds on the prior. The cycles were not "failures" — they were the verification gate doing its job: surfacing real scaffold defects that file-existence checks in Plans 01 + 02 could not catch.

## Verification of Acceptance Criteria

Per plan `<success_criteria>` (15 bullets):

- [x] `dotnet --version` at repo root returns `8.0.421` exactly (literal pinned value; rollForward did not activate)
- [x] `dotnet restore --force --no-cache` exits 0 (after pin correction in d106972)
- [x] `dotnet list tests/BaseApi.Tests/BaseApi.Tests.csproj package` shows xunit.v3 3.2.2, xunit.v3.assert 3.2.2, xunit.runner.visualstudio 3.1.5 — all Requested == Resolved
- [x] `dotnet build --configuration Release` exits 0 with `0 Warning(s)` and `0 Error(s)`
- [x] `dotnet build --configuration Debug` runs unconditionally (W-02) and exits 0 with `0 Warning(s)` and `0 Error(s)`
- [x] Aggregate evaluation per W-02: both configs ran; both passed; aggregate GREEN
- [x] `dotnet test --no-build --configuration Release` reports `Passed: 1, Failed: 0, Skipped: 0, Total: 1` via the Microsoft.Testing.Platform routing (`Run tests:` header, not `VSTest version`)
- [x] `dotnet run --project src/BaseApi.Service --urls http://127.0.0.1:5099` bound the port at iteration 0 (under 1 second)
- [x] Five HTTP probes (`/`, `/api/foo`, `/health/live`, `/random/path`, `/favicon.ico`) all returned HTTP 404
- [x] No 2xx or 5xx response on any probe; process terminated cleanly via `Stop-Process`; no orphan dotnet processes referencing BaseApi.Service
- [x] `appsettings.json` parses as valid JSON via `ConvertFrom-Json`; contains all 4 REQ INFRA-04 sections; locked Service.Name="steps-api" and Service.Version="3.2.0"; no JSON-syntactic comments
- [x] `appsettings.Development.json` parses as valid JSON
- [x] This SUMMARY.md is written documenting all of the above, including the literal resolved SDK version (W-05) and ALL FOUR scaffold deviations
- [x] STATE.md updated to reflect Plan 01-03 closure (next section)
- [x] Phase 1 complete: all 4 ROADMAP success criteria GREEN; INFRA-01/02/03/04 closed.

## User Setup Required

None — Phase 1 is fully self-contained. No external services, no auth, no secrets. Phase 2 (Postgres + Docker Compose) will require Docker Desktop with the WSL2 backend (per STATE.md's existing concern), which is the next setup the user needs to confirm before `/gsd-plan-phase 2`.

## Integration with Plans 01 + 02

Plans 01 and 02 laid down 30 files (the toolchain + the buildable solution + the configuration skeleton). Plan 03 ran the verification battery and uncovered that four canonical scaffold pieces required by the chosen versions (xunit.v3 3.2.2 + SDK 8 + Microsoft.Testing.Platform) had been omitted by the earlier plans. The four fixes are surgical (1 file each for fixes 2/3/4; 1 file for fix 1) and additive — none change behavior that any other code in the repo depends on; they unblock the existing test stack to actually execute.

After Plan 03 closes:
- `Directory.Packages.props` carries the corrected `xunit.runner.visualstudio 3.1.5` pin (was 3.1.7).
- `tests/BaseApi.Tests/BaseApi.Tests.csproj` has 3 new PropertyGroup entries: `OutputType=Exe`, `UseMicrosoftTestingPlatformRunner=true`, `TestingPlatformDotnetTestSupport=true` — the canonical xunit.v3 + SDK 8 MTP setup.
- `01-03-PLAN.md`'s Task 2 acceptance criteria text references the corrected pin.
- `01-03-SUMMARY.md` (this file) documents all four scaffold fixes attributed to the plan that originally introduced the gap (01-01 for the pin; 01-02 for the three MTP properties).

## Next Phase Readiness

Plan 03 closes Phase 1. Phase 2 (Postgres + Docker Compose) is unblocked:

- **Phase 2 next steps:** `/gsd-plan-phase 2` per ROADMAP.md.
- **STATE.md concern carried forward:** Windows Docker Desktop WSL2 backend confirmation needed before any Testcontainers work lands (Phase 8). Phase 2 (compose-based dev DB) will exercise WSL2 enough to flush out misconfiguration.
- **No blockers; no open Phase 1 concerns.**

## Self-Check: PASSED

**File existence verification:**
- FOUND: `C:/Users/UserL/source/repos/SK_P/.planning/phases/01-repository-scaffold/01-03-SUMMARY.md`

**Commit hash verification** (all 5 verification-cycle commits in git log):
- FOUND: `d106972` — `fix(01-01): correct xunit.runner.visualstudio pin from non-existent 3.1.7 to 3.1.5`
- FOUND: `72ba670` — `docs(01-03): update Task 2 acceptance criteria to reference corrected pin`
- FOUND: `630b6ee` — `fix(01-02): add <OutputType>Exe</OutputType> required by xunit.v3 3.2.2 MTP integration`
- FOUND: `e60d001` — `fix(01-02): opt BaseApi.Tests into Microsoft.Testing.Platform runner for dotnet test`
- FOUND: `ea02ad9` — `fix(01-02): route dotnet test through MTP via TestingPlatformDotnetTestSupport`

**Behavioral checks (re-verified post-final-fix):**
- `dotnet --version` returns `8.0.421` (verified)
- `dotnet build --configuration Release` reports `Build succeeded. 0 Warning(s). 0 Error(s).` (verified)
- `dotnet build --configuration Debug` reports `Build succeeded. 0 Warning(s). 0 Error(s).` (verified)
- `dotnet test --no-build --configuration Release` reports `Passed: 1, Failed: 0, Skipped: 0, Total: 1` via MTP routing (verified)
- `dotnet run` boot smoke: 5/5 paths returned 404, process terminated cleanly, no orphans (verified)
- `appsettings.json` parses via `ConvertFrom-Json`; all 4 REQ INFRA-04 sections present; Service.Name="steps-api" Version="3.2.0" (verified)
- `dotnet list package` shows xunit.v3 3.2.2, xunit.v3.assert 3.2.2, xunit.runner.visualstudio 3.1.5 (verified)

**Working tree:** clean as of pre-final-commit (`git status --short` returned empty)

## Task Commits (Plan 03 — verification cycle)

The five commits that landed during Plan 03's verification cycle (in order):

1. **Pin correction (discovered during Task 2 restore):** `d106972` — `fix(01-01): correct xunit.runner.visualstudio pin from non-existent 3.1.7 to 3.1.5` (attribution: Plan 01-01)
2. **Plan-text update (Task 2 acceptance criteria):** `72ba670` — `docs(01-03): update Task 2 acceptance criteria to reference corrected pin`
3. **OutputType=Exe (discovered during Task 2 build):** `630b6ee` — `fix(01-02): add <OutputType>Exe</OutputType> required by xunit.v3 3.2.2 MTP integration` (attribution: Plan 01-02)
4. **MTP runner opt-in (discovered during Task 3a test):** `e60d001` — `fix(01-02): opt BaseApi.Tests into Microsoft.Testing.Platform runner for dotnet test` (attribution: Plan 01-02)
5. **MTP `dotnet test` routing (discovered during Task 3a post-fix test):** `ea02ad9` — `fix(01-02): route dotnet test through MTP via TestingPlatformDotnetTestSupport` (attribution: Plan 01-02; user-approved at checkpoint after 3-attempts guard)

**Plan metadata commit:** _(appended after self-check via final docs commit)_

---
*Phase: 01-repository-scaffold*
*Completed: 2026-05-26*
