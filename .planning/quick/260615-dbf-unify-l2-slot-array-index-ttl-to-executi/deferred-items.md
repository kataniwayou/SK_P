# Deferred Items — quick-260615-dbf

## Out-of-scope pre-existing test failures (NOT caused by this task)

The full `dotnet test tests/BaseApi.Tests` run reported 289 failures / 481 passed / 770 total.
All 289 are pre-existing and out of scope for this hermetic TTL-unification refactor:

1. **Live-stack integration/infra failures (~286).** Integration tests (`*IntegrationTests`),
   Postgres mapper tests (`PostgresExceptionMapperTests`, `SqlStateMappingTests`), bus-dependent
   facts (repeated `Failed to stop bus: rabbitmq://... (Not Started)`), and other tests that
   require a running docker stack (RabbitMQ + Postgres + Redis). The stack was intentionally NOT
   started — this task is explicitly hermetic (plan `<objective>`: "NO docker, NO live stack").

2. **`ComposeYamlFacts.ComposeYaml_ProcessorSample_Sets_Short_ExecutionDataTtl`** —
   expects `Processor__ExecutionDataTtl: "5"` in `compose.yaml`, but the parent commit `da91d32`
   ("fix(68): restore Processor__ExecutionDataTtl 5->300") set it to `"300"`. This is a conflict
   between the Phase-68 compose restore and this compose-config test — pre-existing, independent
   of the pipeline TTL unification (this task does not touch `compose.yaml` or `ComposeYamlFacts`).

### In-scope facts (all PASS)
- All `Pipeline*Facts` (Forward/Recovery/Post/Pre/In/EndDelete) — including the new
  `IndexTtl_EqualsDataTtl_EqualsConfiguredExecutionDataTtl_NoRng` regression guard.
- `EntryStepDispatchConsumerFacts`, `ProcessorOptionsBindingFacts`.
- `dotnet build SK_P.sln -c Release` → 0 warnings, 0 errors (TreatWarningsAsErrors gate).
