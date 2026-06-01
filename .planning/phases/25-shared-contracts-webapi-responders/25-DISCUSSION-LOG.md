# Phase 25: Shared Contracts + WebApi Responders - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-01
**Phase:** 25-shared-contracts-webapi-responders
**Areas discussed:** Not-found response shape, Core join responder hosting, Endpoint/queue naming, "Healthy" constant placement
**Format:** Prose confirm-loop (numbered forks + recommendations), per user preference — single "all A" confirmation.

---

## Not-found response shape (RPC-01/02)

| Option | Description | Selected |
|--------|-------------|----------|
| A | Idiomatic MassTransit dual-response — distinct found + not-found records per query; client uses `GetResponse<TFound, TNotFound>()` | ✓ |
| B | Single nullable response record (`Found` flag / null payload), processor null-checks | |

**User's choice:** A
**Notes:** Chosen because the Phase 26 processor retries-on-not-found until resolved; a typed not-found is cleaner to loop on than a nullable payload. Success records: `{ Id, InputSchemaId?, OutputSchemaId?, ConfigSchemaId? }` and `{ Definition }`.

---

## How the Core publish-only join hosts the responders (RPC-03)

| Option | Description | Selected |
|--------|-------------|----------|
| A | Keep `AddBaseApiMessaging` in `BaseApi.Core`; add optional consumer/endpoint hook; Service owns + passes the consumers | ✓ |
| B | Other split (Service-side registration) | |

**User's choice:** A
**Notes:** Preserves the Phase-19 dependency firewall (Core → `Messaging.Contracts` only). Default no-hook path stays publish-only; CRUD + bus-health-`Degraded`-cap unaffected. Option B was judged to collapse into the same hook since `ConfigureEndpoints` must run inside `UsingRabbitMq`.

---

## Receive-endpoint / queue naming (RPC-03)

| Option | Description | Selected |
|--------|-------------|----------|
| A | Shared queue-name constants in `Messaging.Contracts` mirroring `OrchestratorQueues`; explicit `ReceiveEndpoint`s | ✓ |
| B | Convention-based `ConfigureEndpoints` auto-naming + request-by-type routing, no shared constant | |

**User's choice:** A
**Notes:** Consistent with the established single-source-of-truth convention; Phase 26's request client targets the same constants.

---

## Where the `"Healthy"` constant lives (CONTRACT-03)

| Option | Description | Selected |
|--------|-------------|----------|
| A | New `LivenessStatus` static class in `Messaging.Contracts.Projections` with `public const string Healthy = "Healthy"` | ✓ |
| B | Const directly on the `LivenessProjection` record | |

**User's choice:** A
**Notes:** Mirrors the `L2ProjectionKeys` static-class SoT shape.

---

## Locked without discussion (no real gray area)

- **CONTRACT-01:** Relocate `ProcessorProjection` to `Messaging.Contracts.Projections` + make public. `LivenessProjection` dependency already public → clean lift.
- **CONTRACT-02:** Add `L2ProjectionKeys.ExecutionData(Guid)` → `skp:data:{entryId:D}` with a golden test.

## Claude's Discretion

- No correlation filters on the responder side (stateless query responders, outside the orchestration correlation chain; keeps firewall intact).
- Exact consumer class structure/names/namespaces, DTO→response mapping, response-record file layout, responder-side timeout posture.

## Deferred Ideas

- Processor-side `IRequestClient` usage + retry loops — Phase 26.
- `skp:data:{entryId}` reads/writes — Phase 27.
- Heartbeat worker writing `status: "Healthy"` — Phase 26.
