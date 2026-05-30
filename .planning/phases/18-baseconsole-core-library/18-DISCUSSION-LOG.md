# Phase 18: BaseConsole.Core Library - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-05-30
**Phase:** 18-baseconsole-core-library
**Areas discussed:** ICorrelated stamping, Standalone validation, Health probe wiring, Composition surface

---

## ICorrelated stamping mechanism (CORR-02)

| Option | Description | Selected |
|--------|-------------|----------|
| Stamp MT envelope, keep ICorrelated get-only | Outbound filter sets SendContext/PublishContext.CorrelationId from ambient AsyncLocal, gated on `message is ICorrelated`; no body mutation; frozen contract untouched | ✓ |
| Make ICorrelated settable now + write body field | Add `{ get; set; }`, filter writes the record body; matches research "field + header" wording literally | |
| Defer all outbound stamping to a future phase | Skip CORR-02 mechanics until a real implementer exists | |

**User's choice:** Option A (D-01).
**Notes:** Zero `ICorrelated` implementers this milestone; envelope stamping is idiomatic MassTransit and is exactly what Phase 20's synthetic harness asserts. Resolves Phase 17 D-09's deferred-mutability question by not needing mutability. Frozen `Messaging.Contracts` contract not reopened.

---

## Standalone validation strategy

| Option | Description | Selected |
|--------|-------------|----------|
| Test-only minimal Generic-Host fixture | `ConsoleTestHostFixture` in tests/ composes the AddBaseConsole* chain; proves boot/probes/no-TracerProvider/MT-meter/filters via in-memory harness | ✓ |
| Sample host shipped in the library | A tiny runnable host inside BaseConsole.Core | |
| Defer all runtime validation to Phase 19/20 | Library compiles; prove nothing until the Orchestrator exists | |

**User's choice:** Option A (D-02) + dual-SHA close gate (D-03).
**Notes:** Phase 18 proves boot, live=200 with both deps dead, startup latch, no TracerProvider, MT meter present, filters registered + harness-exercised. Bus-started /ready, two-bus fan-out, ES E2E deferred (need real consumer/broker). Close gate stays dual-SHA + 3-GREEN; triple-SHA (rabbitmqctl) is Phase 20.

---

## Health probe wiring (CONSOLE-HEALTH-01..04)

| Option | Description | Selected |
|--------|-------------|----------|
| appsettings-configurable port (default 8081) + bus-independent hosted Kestrel | ConsoleHealth:Port overridable; listener starts independent of bus so /live answers while bus connects | ✓ |
| Fixed hardcoded health port | Simpler but compose/tests can't override | |

**User's choice:** Option A (D-04) + host-initialized startup latch (D-05).
**Notes:** Console has no DB/migrations → startup gate is the Phase-5-era MarkReady-on-StartAsync variant. Three-way split: startup=host up, ready=MT bus started (auto `ready` check), live=process alive (self-only). MT bus check tagged `ready`, never `live`.

---

## Composition surface (CONSOLE-01, CONSOLE-04)

| Option | Description | Selected |
|--------|-------------|----------|
| Mirror BaseApi.Core: AddBaseConsoleObservability + AddBaseConsole + AddBaseConsoleMessaging(cfg, lambda) + RunAsync | Three calls + run; consumer-registration lambda is the only code parameter; all else via appsettings | ✓ |
| Single monolithic AddBaseConsole | Fewer calls but can't split the IHostApplicationBuilder observability surface | |

**User's choice:** Option A (D-06) + non-generic, all-config-via-appsettings (D-07).
**Notes:** Observability is a separate IHostApplicationBuilder call (needs builder.Logging, same as API D-13). Non-generic (no TDbContext). Service:Name/Version, OTLP endpoint, RabbitMQ host/creds, Redis conn all flow through cfg — console reads its own resource identity (feeds Phase 20 ES proof). No BaseConsole.Core → BaseApi.Core dependency; IStartupGate/Redis duplicated (D-08).

---

## Project location (clarified mid-session)

**User's question:** Is the `src/` folder redundant given all projects share one root?
**Resolution (D-09):** Not redundant and not created this session — `src/` is the locked Phase-1 layout (commit 12a6d90), referenced by SK_P.sln + build props + Dockerfile. All four projects ARE colocated (three under src/, tests under tests/). Visual Studio's Solution Explorer shows a flat virtual tree that does not mirror disk paths — that view is why it "looked different," not a reason to change the layout. New project goes at `src/BaseConsole.Core/`. Flattening src/ recorded as a Deferred Idea (out of Phase 18 scope).

## Claude's Discretion

- Filter registration ordering; ICorrelationAccessor type name + home namespace (new this phase — not created in Phase 17); embedded-health class name + default port + options binding; BaseConsole.Core.csproj shape (CPM + FrameworkReference, Phase 1 inheritance idiom).

## Deferred Ideas

- Flatten src/ to repo root (cross-cutting refactor, NOT adopted); ICorrelated settable properties (Processor milestone); triple-SHA rabbitmqctl gate (Phase 20); extract IStartupGate/Redis to Hosting.Abstractions (when a 3rd host type appears); two-bus fan-out + ES E2E + synthetic harness (Phase 19/20).
