# Phase 62 — Deferred / Out-of-Scope Items

Items discovered during execution that are out of scope for the current plan.

## From Plan 62-02 execution (2026-06-13)

### DI-62-A — Stale compose guard `ComposeYamlFacts.ComposeYaml_Has_ProcessorSample_Service_Block` fails after the Plan 62-01 reshape

- **Discovered during:** Plan 62-02, Task 2 hermetic-suite verification (`--filter-not-trait Category=RealStack`).
- **Failure:** `Assert.Contains("container_name: sk-processor-sample", content)` — `Not found: "container_name: sk-processor-sample"`.
- **Root cause:** Plan **62-01** intentionally reshaped the `processor-sample` compose tier — it DELETED `container_name: sk-processor-sample` and ADDED `deploy.replicas: 2` (commit `de40b89` `feat(62-01): reshape processor-sample to deploy.replicas:2`). The Phase-28 (SAMPLE-02) guard at `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs:133-139` was NOT updated to match — it still asserts the now-deleted `container_name` line.
- **Why deferred (out of scope for 62-02):** Plan 62-02's `files_modified` is restricted to the two `tests/BaseApi.Tests/Orchestrator/*E2ETests.cs` files (the fabricated-key gate-verdict tests + the seeding helper). `ComposeYamlFacts.cs` and `compose.yaml` are Plan 62-01 surface. This is a Plan-62-01 test-update gap, not a regression introduced by Plan 62-02. The single failure is pre-existing relative to Plan 62-02's first commit.
- **Suggested fix (Plan 62-01 territory):** Update `ComposeYaml_Has_ProcessorSample_Service_Block` to assert the new shape — e.g. drop the `container_name` assertion and add a `deploy.replicas: 2` regex scoped to the `processor-sample` block (mirror the existing `ComposeYaml_Keeper_Declares_Two_Replicas` tempered-greedy regex at `:173-180`).
- **Status:** OPEN — out of scope for 62-02; addressable via a 62-01 follow-up or `/gsd-code-review-fix 62`.
