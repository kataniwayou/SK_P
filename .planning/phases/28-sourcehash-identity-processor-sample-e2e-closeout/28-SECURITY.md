# SECURITY.md — Phase 28: sourcehash-identity-processor-sample-e2e-closeout

**Audit date:** 2026-06-02
**ASVS Level:** default (dev-stack / build-tooling surface; no public auth boundary)
**Threats Closed:** 13/13
**Result:** SECURED

---

## Threat Verification

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-28-01 | Elevation | accept | CLOSED | See Accepted Risks log below. Rationale holds: task is in-repo, code-reviewed, netstandard2.0 surface only (SHA256.Create / File.ReadAllText / StringComparer.Ordinal), no shell-out, no file writes outside obj/. No contradicting code found. |
| T-28-02 | Tampering | mitigate | CLOSED | `src/BaseProcessor.Core/SourceHash.targets` lines 46, 47, 55: forward-slash path normalization (`Replace('\\', '/')`), `paths.Sort(StringComparer.Ordinal)`, LF content normalization (`text.Replace("\r\n", "\n").Replace("\r", "\n")`). Dual-build verifier (`scripts/verify-sourcehash-reproducible.ps1`) proved host==docker (`ab923430…3219a8`) with exit 0. |
| T-28-03 | Spoofing | mitigate | CLOSED | `SourceHash.targets` line 65: `b.ToString("x2")` emits lowercase 64-hex. `tests/BaseApi.Tests/Processor/SourceHashEmbedFacts.cs` line 26: `Assert.Matches(new Regex("^[a-f0-9]{64}$"), hash)` — reflection fact (not recompute) asserts the shape and gates the runtime reader's fail-fast. |
| T-28-04 | Information Disclosure | accept | CLOSED | See Accepted Risks log below. Rationale holds: reader (AssemblyMetadataSourceHashProvider, unchanged from Phase 26) throws `InvalidOperationException` naming only the KEY "SourceHash" — the value is never in the error path. No contradicting code found. |
| T-28-05 | Tampering | mitigate | CLOSED | `scripts/verify-sourcehash-reproducible.ps1` (full file): builds host SDK and Linux Docker separately, extracts embedded hash from both dlls via PE metadata reader, asserts byte-equality, exits 0 on match. 28-02-SUMMARY.md records PROVEN exit-0 run with byte-identical hashes. |
| T-28-06 | Information Disclosure | mitigate | CLOSED | `src/Processor.Sample/Dockerfile` lines 17-24: selective csproj-first restore COPY closure (Messaging.Contracts + BaseConsole.Core + BaseProcessor.Core + Processor.Sample csproj files only), then `COPY src/ src/`. No `.env`, credentials, secrets, or credential files are present in the COPY layers. Dev guest/guest broker creds are injected at runtime via compose environment block, not baked into the image. No secrets pattern matched in Dockerfile. |
| T-28-07 | Denial of Service | accept | CLOSED | See Accepted Risks log below. Rationale holds: ProcessorStartupOrchestrator's unbounded boot-before-register retry is the established Phase 26 behaviour. The container stays Up (only /ready red) until a DB row is seeded; the compose `start_period: 30s` + Plan 04 pre-flight seed resolve the steady-state. No contradicting code found. |
| T-28-08 | Spoofing | mitigate | CLOSED | `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs`: grep for `SeedHostProcessorLive` returns zero matches (confirmed). `PollForHealthyLivenessAsync` (lines 172-211) polls the REAL container's `skp:{procId:D}` Redis key with freshness check; Start is driven only after the genuine heartbeat is present. |
| T-28-09 | Spoofing | mitigate | CLOSED | `SampleRoundTripE2ETests.cs` lines 98-100: genuine hash reflected via `GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "SourceHash")`. Grep for `RandomSha256Hex`, `ComputeHash`, `SHA256` returns zero matches (confirmed). `SeedProcessorAsync` (line 293) passes that hash as `SourceHash:` in the CRUD payload, gated by the DB `^[a-f0-9]{64}$` validator on the API side. |
| T-28-10 | Tampering | mitigate | CLOSED | `compose.yaml` line 246: `Processor__ExecutionDataTtl: "5"` (short TTL, 5s vs default 300s). `SampleRoundTripE2ETests.cs` line 142: `factory.L2KeysToCleanup.Add(newDataKey!.Value)` (net-zero teardown for the minted execution-data key, drained in `DisposeAsync`). 28-04-SUMMARY.md records redis SHA held BEFORE==AFTER (`56e9e516…`). |
| T-28-11 | Spoofing | mitigate | CLOSED | `scripts/phase-28-close.ps1` lines 86-111: GET-or-create on `/api/v1/processors/by-source-hash/{sourceHash}` — on 200 reuses existing `procId`, on 404 POSTs create. The unique `uq_processor_source_hash` index rejects a duplicate hash at the DB layer. 28-04-SUMMARY.md records stable procId `4315324c-…` reused across all 3 gate runs; redis and rabbitmq SHAs held. |
| T-28-12 | Tampering | mitigate | CLOSED | Short `Processor__ExecutionDataTtl: "5"` in compose (T-28-10 partner) + `L2KeysToCleanup` teardown (T-28-10 partner). Gate in `phase-28-close.ps1` lines 252-263: redis `--scan` SHA BEFORE vs AFTER assertion; mismatch exits 1 (fail-closed). 28-04-SUMMARY.md records SHA held (`56e9e516…`); no leaked `skp:data:*` keys. No `FLUSHDB` in the gate script (confirmed zero matches). |
| T-28-13 | Repudiation | accept | CLOSED | See Accepted Risks log below. Rationale holds: `phase-28-close.ps1` prints all three SHA-256 values + run counts (lines 282-285); 28-04-SUMMARY.md records them (psql `b48ce783…`, redis `56e9e516…`, rabbitmq `67a92f45…`, 395 facts x3). Operator-authorized, auditable. |

---

## Accepted Risks Log

| Threat ID | Category | Accepted Risk | Rationale | Owner |
|-----------|----------|---------------|-----------|-------|
| T-28-01 | Elevation | RoslynCodeTaskFactory inline task executes at build time with MSBuild trust | The task is authored in-repo, code-reviewed, uses netstandard2.0 surface only (SHA256.Create, File.ReadAllText, StringComparer.Ordinal). No shell-out; writes only to `$(IntermediateOutputPath)sourcehash.stamp` inside obj/. This is the standard posture for shared MSBuild inline tasks. Standard build-tooling trust level; no external code loaded. | dev-team |
| T-28-04 | Information Disclosure | Reader fail-fast names only the key, not the hash value | `AssemblyMetadataSourceHashProvider` (Phase 26, unchanged) throws with the string "SourceHash" as the missing-key name. The 64-hex value is never interpolated into the error message, so a caught exception reveals no identity. Risk is low (dev-stack only, no public auth boundary). | dev-team |
| T-28-07 | Denial of Service | processor-sample boots before DB row exists (chicken-and-egg) | ProcessorStartupOrchestrator retries indefinitely on "Processor row not yet registered for hash". Container stays Up (only /ready red) until a row is seeded. The close gate's pre-flight idempotent seed (Plan 04) ensures the row exists before steady-state operations. No production workload is at risk; this is a dev-stack composition concern. | dev-team |
| T-28-13 | Repudiation | Gate evidence is operator-recorded, not cryptographically signed | The close gate prints SHA-256 values and run counts; they are copy-pasted into SUMMARY.md by the human operator. A compromised operator could falsify them. For a dev-stack phase gate this risk is accepted; no external audit chain is required. | dev-team |

---

## Unregistered Threat Flags

None — no `## Threat Flags` sections were present in any of the four SUMMARY files (28-01 through 28-04). No executor-detected attack surface was flagged outside the registered threat register.

---

## Notes

- The `## Threat Flags` check was performed against all four SUMMARY files. No flags exist.
- T-28-02 and T-28-05 both relate to cross-OS hash divergence (algorithm normalization vs. dual-build proof respectively) and are both CLOSED by complementary controls: the normalizations in `SourceHash.targets` and the exit-0 dual-build run recorded in 28-02-SUMMARY.md.
- T-28-08 and T-28-09 are grep-enforced at the acceptance-criteria level: zero occurrences of `SeedHostProcessorLive`, `RandomSha256Hex`, `ComputeHash`, and `SHA256` in `SampleRoundTripE2ETests.cs` were confirmed.
- Implementation files were not modified during this audit.
