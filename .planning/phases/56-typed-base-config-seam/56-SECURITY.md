---
phase: 56-typed-base-config-seam
auditor: gsd-secure-phase
asvs_level: not-configured
block_on: not-configured
verified: 2026-06-12
status: SECURED
threats_total: 3
threats_closed: 3
threats_open: 0
---

# Phase 56: Security Audit Report

**Trust boundary:** dispatch message payload (string) → `BaseProcessor<TConfig>.ExecuteAsync` →
`JsonSerializer.Deserialize<TConfig>`. The payload is orchestration-admitted (Gate B schema-validated
upstream) but is the untrusted-shaped input at this seam.

## Threat Verification

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-56-01 | Denial of Service / Tampering | mitigate | CLOSED | See below |
| T-56-02 | Tampering / Information Disclosure | mitigate | CLOSED | See below |
| T-56-03 | Tampering | accept | CLOSED | See below |

---

### T-56-01 — Malformed payload deserialize (DoS / Tampering) — CLOSED

**Mitigation pattern verified:** `JsonException` is NOT caught locally in `BaseProcessor<TConfig>`; it
propagates to the pipeline `catch (Exception ex)` block which maps it to exactly one business
`StepFailed`, logs it, aborts the batch, and does not crash outside the pipeline or mis-route to Keeper.

**Evidence:**

- `src/BaseProcessor.Core/Processing/BaseProcessor`1.cs` line 19–26: `internal sealed override
  ExecuteAsync` body has no `try` block. `JsonSerializer.Deserialize<TConfig>(payload,
  ProcessorConfig.SerializerOptions)` at line 24 is called bare — no surrounding try/catch.
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` line 241–246: `catch (Exception ex)` is the
  catch-all that receives the `JsonException`. It calls `SendResult(BuildFailed(d, ex.Message), ...)` and
  `DeleteTerminalAsync(...)` then returns — exactly one `StepFailed`, no Keeper send, no un-acked
  redelivery, no exception escaping the pipeline.
- `tests/BaseApi.Tests/Processor/PipelineInFacts.cs` lines 39–58:
  `MalformedPayload_DeserFailure_Emits_Single_StepFailed` drives a REAL `BaseProcessor<DeserConfig>`
  subclass (`RealDeserProcessor`) with payload `"not json"` through `pipeline.RunAsync`. Asserts
  `Assert.Single(send.Sent)` + `Assert.IsType<StepFailed>(sent)` + `Assert.Empty(send.SentKeeper)`.
  Test passed in the 530/530 hermetic suite (phase gate, 2026-06-12).

---

### T-56-02 — Empty/whitespace/absent payload (Tampering / Information Disclosure) — CLOSED

**Mitigation pattern verified:** `string.IsNullOrWhiteSpace(payload)` guard short-circuits to `null`
config BEFORE `Deserialize` is ever called. Config-less path is deterministic; author owns null-handling.

**Evidence:**

- `src/BaseProcessor.Core/Processing/BaseProcessor`1.cs` line 22–24:
  ```
  TConfig? config = string.IsNullOrWhiteSpace(payload)    // D-04 guard BEFORE deserialize
      ? null
      : JsonSerializer.Deserialize<TConfig>(payload, ProcessorConfig.SerializerOptions);
  ```
  `Deserialize` is unreachable on an empty/whitespace string. The ternary short-circuits to `null`,
  which is passed as `config` to the typed `ProcessAsync`.
- `tests/BaseApi.Tests/Processor/PipelineInFacts.cs` and `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs`:
  The blank-config path is proven by `SampleProcessorFacts.ProcessAsync_Null_Config_Falls_Back_To_Fixed_Token`
  (confirmed green in phase verification, 2026-06-12; `(SampleConfig?)null` → `"processor-sample-ok"`).

---

### T-56-03 — Extra / differently-cased JSON properties (Tampering) — CLOSED (accepted)

**Accepted risk description (from threat register):** The shared `JsonSerializerOptions` is deliberately
forgiving — case-insensitive, unknown members silently ignored. An admitted payload with extra keys
deserializes without error (keys ignored). Strictness is Gate B's job (upstream, unchanged) and Gate A's
job (Phase 57). `JsonUnmappedMemberHandling.Disallow` is explicitly OUT of scope for this phase.
No PII, no auth, no new persistence/network surface.

**Configuration match confirmed:**

- `src/BaseProcessor.Core/Configuration/ProcessorConfig.cs` lines 18–22:
  ```csharp
  public static readonly JsonSerializerOptions SerializerOptions = new()
  {
      PropertyNameCaseInsensitive = true,   // D-05
      // default unknown-member handling = ignore (do NOT set JsonUnmappedMemberHandling.Disallow) — D-05
  };
  ```
  `PropertyNameCaseInsensitive = true` — case-insensitive confirmed.
  `JsonUnmappedMemberHandling` is NOT set — unknown-member handling defaults to `Skip` (ignored) confirmed.
  No `JsonUnmappedMemberHandling.Disallow` present (the accepted-risk carve-out is intact).

The accepted-risk description in the threat register accurately matches the implemented configuration.

---

## Unregistered Flags

No `## Threat Flags` section was present in either SUMMARY.md. No unregistered flags to record.

## Accepted Risks Log

| Threat ID | Category | Accepted Risk | Rationale | Deferred To |
|-----------|----------|---------------|-----------|-------------|
| T-56-03 | Tampering | Extra/unknown JSON keys in payload silently ignored (no `JsonUnmappedMemberHandling.Disallow`) | Forgiving deserialization is by design; strictness is Gate B (upstream) and Gate A (Phase 57); no PII/auth/persistence surface | Phase 57 Gate A |

---

*Audited: 2026-06-12*
*Auditor: gsd-secure-phase (Claude Sonnet 4.6)*
