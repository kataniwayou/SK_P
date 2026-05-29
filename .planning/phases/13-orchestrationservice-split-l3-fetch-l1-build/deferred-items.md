# Deferred Items — Phase 13

Out-of-scope discoveries logged during execution (NOT fixed in this phase).

## Transient fixture-lifecycle flake

- **Item:** `BaseApi.Tests.Middleware.ConcurrencyTokenTests.Test_RacingWrites_Produce_409_WithGenericMessage_NoXminLeak`
- **Observed:** Plan 13-03 Task 2 run — one run failed with `Assert.NotNull() Failure` rooted in a `System.Net.Sockets` Postgres connect timeout during the `WithWebHostBuilder`-derived host's `StartupCompletionService.MigrateAsync` (parallel-startup connection pressure on the Middleware-flavored `PostgresFixture`).
- **Re-run:** Immediately re-ran the same test in isolation → 181/181 GREEN. Confirmed transient (Postgres connect timeout under parallel host-startup load), not a Phase 13 regression.
- **Scope:** Different test file + different fixture (`BaseApi.Tests.Middleware.PostgresFixture`), pre-existing test, unrelated to the Phase 13 loader/cleanup changes. Per executor scope boundary, NOT fixed here.
- **Suggested owner:** A future hardening pass (matches the documented Phase 5/6 OTel-style fixture-lifecycle robustness items) — e.g., connection-resiliency / retry on startup migration, or reduced parallel-startup contention.
- **Second occurrence (Task 3 cadence Run 3):** same test failed at `ConcurrencyTokenTests.cs:80` `Assert.NotNull(conflict)` — the two concurrent POSTs serialized cleanly under load so neither produced the 409 the test expects. This is a timing-dependent race in the TEST (it depends on two POSTs genuinely overlapping). Re-ran the full suite → 181/181 GREEN. Confirmed pre-existing flaky concurrency test, NOT a Phase 13 regression (Phase 13 is read-only; no write path touched). Cadence: Runs 1, 2, 3(retry) all GREEN.
