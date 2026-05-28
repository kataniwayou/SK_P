# Pitfalls Research — Milestone v3.3.0 (Orchestration L3 → L1 → L2 Build Pipeline)

**Domain:** .NET 8 Web API adding Redis-backed L2 materialized projection + transient in-memory L1 build pipeline + JSON-Schema validation gates on top of a hardened v3.2.0 base (EF Core 8 + Npgsql + OTel-logs-to-ES + OTel-metrics-to-Prom + NO traces backend + RFC 7807 + X-Correlation-Id middleware + 142/142 GREEN cadence + per-class throwaway Postgres + Mapperly RMG codes promoted to errors).
**Researched:** 2026-05-28
**Confidence:** HIGH — All claims verified against (a) the prior v3.2.0 PITFALLS.md inventory (39 pitfalls already mitigated), (b) StackExchange.Redis official docs + GitHub issues #2537/#1169/#885, (c) OpenTelemetry.Instrumentation.StackExchangeRedis README + issues #1301/#674/#3257, (d) JsonSchema.Net (gregsdennis/json-everything) SchemaRegistry docs + 2025 perf work, (e) Redis canonical commands docs (RENAME, SCAN, MULTI/EXEC), (f) AspNetCore.HealthChecks.Redis 9.0.0 package surface. MEDIUM only where Cluster-mode or Azure-Managed-Redis edge-case behavior may vary by deployment posture (sk_p targets single-node Redis in Compose for v3.3.0).

This file is **the v3.3.0 delta**. It does NOT re-list the 39 pitfalls already locked in the prior v3.2.0 PITFALLS.md (Postgres timestamptz UTC, DbContext lifetime, snake_case migrations, xmin token, FluentValidation auto-validation deprecation, MEL→OTLP wiring, AspNet probe filtering, SQLSTATE → HTTP mapping, JSON Schema draft 2020-12 + SSRF disabled at Phase 8, Compose healthcheck ordering, etc.). Where this milestone is at risk of regressing one of those v3.2.0 invariants, the pitfall here calls it out explicitly (see Pitfalls 7, 11, 14, 15, 31 — each lists the originating v3.2.0 invariant it guards).

Phase-name placeholders used below — these are *suggested* phase names that `/gsd-plan-milestone` will refine:

- **R0 — Redis infrastructure** (`compose.yaml` Redis service, `StackExchange.Redis` package, `ConnectionMultiplexer` DI wiring, `appsettings` connection string + `AbortOnConnectFail=false`)
- **R1 — L1 build pipeline** (transient `Dictionary<Guid, EntityDto>` populated inside `OrchestrationService.StartAsync`, scoped lifetime, try/finally teardown)
- **R2 — Workflow graph traversal** (iterative DFS with cycle detection, schema-edge compatibility gate)
- **R3 — Payload↔ConfigSchema validation gate** (JsonSchema.Net 2020-12, cached per ConfigSchemaId, SSRF stays disabled)
- **R4 — L2 Redis writer** (3 key spaces, idempotent overwrite semantics, last-write-wins)
- **R5 — Stop endpoint** (idempotent L2 eviction, key-chain walk vs SCAN tradeoff)
- **R6 — Observability extension** (Redis instrumentation discipline given NO traces backend; healthcheck tag discipline; correlation propagation through Redis async ops; startup-probe extension)
- **R7 — Test fixtures** (per-class Redis isolation matching the Postgres `stepsdb_test_*` discipline)

---

## Critical Pitfalls

### Pitfall 1: `ConnectionMultiplexer` registered Scoped (or worse, Transient) — connection storm on startup

**What goes wrong:**
First request after deploy works. Second request opens another TCP connection. Tenth concurrent request opens 10. Redis hits `maxclients` and the API logs `RedisConnectionException: No connection is active/available to service this operation` and starts returning 500s. Restarting the API "fixes" it for 30 seconds.

**Why it bites (root cause):**
`StackExchange.Redis.ConnectionMultiplexer` is explicitly designed to be a **process-wide singleton** that multiplexes all callers through a single set of TCP connections. The class is thread-safe; `IDatabase` instances obtained from `.GetDatabase()` are cheap handles into the multiplexer and are also safe to share or recreate per-call. Registering the multiplexer as Scoped means one new TCP handshake per HTTP request; Transient means one per resolution. Neither matches the multiplexer's design contract.

In the sk_p codebase this is a **new dependency** — there is no existing pattern to copy; the path of least resistance is `services.AddScoped<IConnectionMultiplexer>(...)` because every other infrastructure dependency in `BaseApi.Core` is Scoped or Transient. That's the exact wrong choice.

**Which phase addresses it:**
**R0 — Redis infrastructure.** First file in the milestone that touches DI must encode the singleton.

**Prevention strategy (concrete):**
- DI registration:
  ```csharp
  services.AddSingleton<IConnectionMultiplexer>(sp =>
  {
      var cfg = sp.GetRequiredService<IConfiguration>();
      var opts = ConfigurationOptions.Parse(cfg.GetConnectionString("Redis")!);
      opts.AbortOnConnectFail = false;        // see Pitfall 2
      opts.ClientName = "sk-api";              // shows up in CLIENT LIST for ops
      return ConnectionMultiplexer.Connect(opts);
  });
  ```
- Add an xUnit fact in `tests/BaseApi.Tests/Composition/RedisLifetimeFacts.cs` that resolves `IConnectionMultiplexer` from the root `IServiceProvider` and from a created scope, asserts reference-equality (`Assert.Same(rootMux, scopedMux)`). This makes a regression to Scoped registration a red test.
- Add an architectural fact that loads `ServiceCollection` descriptors via `services.BuildServiceProvider()` and asserts `IConnectionMultiplexer`'s descriptor has `ServiceLifetime.Singleton` — defense in depth.

**Warning signs (code review):**
- `services.AddScoped<IConnectionMultiplexer>` or `services.AddTransient<IConnectionMultiplexer>` — must be Singleton.
- `using (var mux = ConnectionMultiplexer.Connect(...))` in a controller/service/handler — disposes the multiplexer at end of request.
- Redis ops log `Timeout`/`No connection` that disappear after API restart (connection-leak symptom).
- `CLIENT LIST` against Redis shows ≥1 connection per request (should be ≤ a handful per API instance).

---

### Pitfall 2: `AbortOnConnectFail` not explicitly set — startup gate fails when Redis is briefly unavailable

**What goes wrong:**
StackExchange.Redis's default `AbortOnConnectFail=true` makes the singleton factory throw `RedisConnectionException` if Redis isn't reachable at first connect. `Program.cs` propagates the throw; the process crashes; K8s/Compose restarts the container; Redis takes 2 more seconds to be ready; crash again. Restart loop until Redis stabilizes (30-90 seconds), and during that window `/health/live` is also down (process isn't running), which violates the v3.2.0 Phase 5 HEALTH-01 contract ("live never depends on external state").

**Why it bites (root cause):**
`AbortOnConnectFail=true` is appropriate for short-lived CLI tools (fail fast at startup) but wrong for long-lived services where Redis is a runtime dependency. Azure Managed Redis and most managed providers set `abortConnect=false` by default in generated connection strings; local-dev / non-cloud Redis does NOT. The sk_p stack is local-Compose-first; without explicit configuration the local default bites.

This pitfall also interacts with the v3.2.0 PITFALLS.md Pitfall 15 ("liveness probe must not touch external state"). If Redis is briefly down at startup and the multiplexer throws, the whole process dies — *strictly worse* than a 503 on `/health/ready`.

**Which phase addresses it:**
**R0 — Redis infrastructure.**

**Prevention strategy (concrete):**
- Always parse the connection string into `ConfigurationOptions` and explicitly set:
  ```csharp
  opts.AbortOnConnectFail = false;
  opts.ConnectRetry = 3;
  opts.ConnectTimeout = 5000;
  opts.SyncTimeout = 5000;
  opts.AsyncTimeout = 5000;
  opts.KeepAlive = 60;
  ```
- Subscribe to `ConnectionFailed` and `ConnectionRestored` in the singleton factory and log via `ILogger<ConnectionMultiplexer>` so OTel → ES carries the events with the v3.2.0 `service.name=sk-api` resource tag intact.
- The integration-test `RedisFixture` MUST start the Redis container **before** `WebApplicationFactory.CreateClient()` is called — otherwise the test process itself hits the same race.
- Document in the connection-string section of `appsettings.json` (via a comment in `appsettings.README.md` since STJ is strict per v3.2.0 PITFALLS Pitfall 30) that `AbortOnConnectFail=false` is intentional.

**Warning signs (code review):**
- `ConnectionMultiplexer.Connect("localhost:6379")` (raw string overload — defaults apply).
- API container restart-loops during `docker compose up` while Redis is still in `starting` state.
- Logs show `RedisConnectionException: It was not possible to connect to the redis servers; to create a disconnected multiplexer, disable AbortOnConnectFail`.

---

### Pitfall 3: TLS / SSL not configured for non-local Redis — silent plaintext leak or handshake failure

**What goes wrong:**
Local-dev Redis runs plaintext on `:6379`. Staging/prod migrates to TLS-only Redis (Azure Managed Redis, Redis Enterprise Cloud, AWS ElastiCache in-transit-encrypted). The connection string isn't updated for TLS, and either (a) handshake fails with a cryptic `SocketException` that looks like a network issue, or (b) — on a misconfigured server — the connection downgrades to plaintext on the wrong port and Redis credentials cross the network in the clear.

**Why it bites (root cause):**
StackExchange.Redis's `Ssl` and `SslHost` options must be set explicitly; they are NOT auto-inferred from port number. The Redis protocol does not negotiate TLS in-band — the client must commit to plaintext or TLS before any bytes are exchanged.

Compounding factor: v3.2.0 has zero TLS config anywhere in the stack (Postgres also runs plaintext in Compose). The team has no muscle memory for TLS knobs in this codebase.

**Which phase addresses it:**
**R0 — Redis infrastructure.**

**Prevention strategy (concrete):**
- `ConfigurationOptions.Parse` understands `ssl=true` in the connection string. Use `,ssl=true,sslprotocols=tls12|tls13` for prod connection strings. Validate at startup that if the host is NOT `localhost` / `127.0.0.1` / a Compose service name, then `opts.Ssl == true` — fail fast on the inverse.
- The `appsettings.{Environment}.json` matrix should make this explicit:
  - `appsettings.Development.json`: `"Redis": "localhost:6379,abortConnect=false"` (plaintext OK locally)
  - `appsettings.Production.json`: `"Redis": "{HOST}:6380,ssl=true,abortConnect=false,sslProtocols=tls12|tls13"`
- Set `opts.CertificateValidation += (sender, cert, chain, errors) => { /* log + return errors == None */ };` so cert errors are observable, not silently bypassed.

**Warning signs (code review):**
- Connection strings without `ssl=true` for non-localhost hosts.
- `opts.CertificateValidation = (_, _, _, _) => true;` (accepts ANY cert — defeats TLS).
- Plaintext `:6379` in a production environment-variable.

---

### Pitfall 4: MULTI/EXEC misunderstood as a transactional read-modify-write — silent data loss

**What goes wrong:**
Developer thinks `ITransaction.Execute()` provides ACID-like isolation around reads and writes:
```csharp
var tx = db.CreateTransaction();
var current = await tx.HashGetAsync(key, "version");   // returns default — see below
if ((int)current < newVersion)
    tx.HashSetAsync(key, "version", newVersion);
await tx.ExecuteAsync();
```
`await tx.HashGetAsync(...)` does NOT execute the GET against Redis — it **queues** it. The result inside the transaction is unavailable until `ExecuteAsync()` completes. The `if` branch reads `null`/`0`, the SET always runs, and concurrent writers all overwrite each other unconditionally.

**Why it bites (root cause):**
Redis MULTI/EXEC queues commands and executes them atomically as a batch — but **the result of each queued command is not known until the batch completes**. StackExchange.Redis's transaction object exposes only async methods that return `Task<T>` which **completes after `ExecuteAsync`** — reading mid-transaction returns default. This is documented in [StackExchange.Redis Transactions.md](https://github.com/StackExchange/StackExchange.Redis/blob/main/docs/Transactions.md): "you can't make decisions inside the transaction."

For optimistic concurrency, the correct primitive is `Condition.HashEqual(...)` / `Condition.KeyExists(...)` added via `tx.AddCondition(...)` before `ExecuteAsync` — these translate to WATCH and the transaction aborts (returns `false` from `ExecuteAsync`) if any watched key was modified.

Related trap: a transaction with **zero queued commands** is a no-op that returns `true` — code that conditionally adds commands inside an `if` can ship a phantom "succeed even though I did nothing" path.

**Which phase addresses it:**
**R4 — L2 Redis writer.**

**Prevention strategy (concrete):**
- For v3.3.0 the explicit decision is **"last-write-wins, no Redis lock"** (PROJECT.md line 22). Therefore: **do not use MULTI/EXEC at all in this milestone.** Use plain `IDatabase` pipelined writes via `db.CreateBatch()` for grouping multiple SETs into one round-trip; do not request transactional semantics you have explicitly opted out of.
- If a *future* milestone needs read-modify-write, use `Condition.HashEqual(...)` or a Lua script via `db.ScriptEvaluateAsync(...)` — never the queue-then-await-the-Task pattern above.
- Add a code-search guard in CI for the v3.3.0 branch: grep for `CreateTransaction` in `src/` and flag any introduction for review.

**Warning signs (code review):**
- Any `var tx = db.CreateTransaction();` in v3.3.0 source.
- `await tx.GetAsync(...)` whose result is read before `await tx.ExecuteAsync()`.
- A transaction that conditionally queues commands inside an `if`.
- Reads of `Task<T>.Result` from a transaction-returned task before `Execute` has been awaited.

---

### Pitfall 5: DEL-then-SET (or SET-then-DEL-old) atomic-overwrite anti-pattern — readers see missing or stale-mixed state

**What goes wrong:**
The v3.3.0 contract is "Start is idempotent (PUT-like); a second Start for the same WorkflowIds re-runs the pipeline and replaces L2 keys" (PROJECT.md line 22). Naive implementation:
```csharp
await db.KeyDeleteAsync(workflowKey);                              // window opens
await db.StringSetAsync(workflowKey, JsonSerialize(newDto));       // window closes
```
Between the two awaits — typically 1-5ms, unbounded under network delay — any external consumer reading `{workflowId}` gets `nil` and may interpret it as "workflow was stopped," triggering corrective action (alert, page, retry storm).

Mirror anti-pattern:
```csharp
await db.StringSetAsync(workflowKey, JsonSerialize(newDto));       // overwrite root
await db.KeyDeleteAsync(oldStepIdKeys);                            // children for OLD step list
await db.StringSetAsync(newStepIdKeys, JsonSerialize(newChildren)); // children for NEW list
```
Readers see new root pointing at new entryStepIds, but new `{workflowId:stepId}` children don't exist yet — chain navigation hits `nil` partway through.

**Why it bites (root cause):**
Redis is single-threaded per node, so each individual command is atomic — but a sequence of commands is NOT. Any non-atomic transition leaves a visible inconsistent state. The user explicitly accepted "last-write-wins on concurrent Starts" (PROJECT.md line 22), but that's a *concurrent-write* concern; the *single-write* atomic-overwrite hole is a separate, fixable bug.

The canonical safer pattern is **stage-then-RENAME**: build the new value under a temp key (`{workflowId}.staging.{guid}`), then `RENAME` it onto the production key. RENAME is atomic per [Redis docs](https://redis.io/docs/latest/commands/rename/): readers see either the old complete value or the new complete value, never `nil` or partial.

RENAME traps:
- `RENAME nonexistent_key target` raises an error ("no such key"). Code must check existence or use `RENAMENX` semantics carefully.
- Staging key prefix matters: in Redis Cluster (not v3.3.0's target but a documented future risk), staging and target keys must hash to the same slot via `{hashtag}` braces. The existing key shape `{workflowId}` and `{workflowId:stepId}` is hashtag-compatible; the staging key MUST preserve the same hashtag (`{workflowId}.staging` not `staging.{workflowId}`).

**Which phase addresses it:**
**R4 — L2 Redis writer.**

**Prevention strategy (concrete):**
- For the single `{workflowId}` root key: stage-then-RENAME pattern:
  ```csharp
  var stagingKey = $"{{{workflowId:N}}}.staging.{Guid.NewGuid():N}";
  await db.StringSetAsync(stagingKey, JsonSerialize(newDto));
  await db.KeyRenameAsync(stagingKey, $"{{{workflowId:N}}}");  // atomic overwrite
  ```
- For the N `{workflowId:stepId}` children: independent keys; for "replace all children for a workflow" semantics:
  1. SET all new child keys under staging names.
  2. Determine the set of OLD child keys to delete (needs `allStepIds[]` on the root — see Pitfall 12).
  3. Use an `IBatch` to issue RENAME-new + DEL-old in one round-trip. Atomicity per-key still holds; cross-key atomicity does not — but the inconsistency window is bounded to a single RTT instead of the full pipeline duration.
- Document in the PR that "concurrent Starts can interleave per-key writes; reader may see a `{workflowId}` root that mismatches its `{workflowId:stepId}` children — this is the accepted last-write-wins surface." Add a code comment referencing this PITFALLS entry (and Pitfall 20 below).
- Do NOT use a global lock to serialize Starts; the user has explicitly chosen lock-free semantics.

**Warning signs (code review):**
- `KeyDeleteAsync(key); StringSetAsync(key, ...);` (the canonical bug).
- Any L2 write that takes more than one RTT between "old visible" and "new visible" without staging + RENAME.
- Hash-tag-incompatible staging key names (e.g., `staging:{workflowId}` instead of `{workflowId}.staging`).

---

### Pitfall 6: Per-request state on a singleton `OrchestrationService` — L1 dictionary leaks across concurrent Starts

**What goes wrong:**
v3.2.0's `OrchestrationService` (created in Phase 9) is registered Scoped. A developer adding the L1 dictionary as a class-level field (`private readonly Dictionary<Guid, EntityDto> _l1 = new();`) inadvertently switches the service to Singleton "because the dictionary shouldn't be re-created per request." Two concurrent `POST /api/v1/orchestration/start` calls now share the same dictionary instance; entities from request A leak into request B's traversal; cycle detection produces phantom cycles or misses real ones; schema-edge gate misfires; payload validation runs against the wrong schema.

**Why it bites (root cause):**
The L1 dictionary IS per-request state by contract (PROJECT.md line 24: "explicit teardown of the in-memory dictionary + temp traversal lists at the end of `StartAsync` (success or failure path)"). It must NOT be shared. v3.2.0's `OrchestrationService` is correctly Scoped per Phase 9 wiring, but the convenience of "store it as a field" leads to the wrong lifetime change.

The safer pattern: L1 dictionary is a **local variable** inside `StartAsync`, passed by reference (or wrapped in a small `OrchestrationContext` record) into helper methods. Structurally impossible to leak regardless of service lifetime.

**Which phase addresses it:**
**R1 — L1 build pipeline.**

**Prevention strategy (concrete):**
- L1 dictionary is a local variable in `OrchestrationService.StartAsync`:
  ```csharp
  public async Task<IActionResult> StartAsync(List<Guid> workflowIds, CancellationToken ct)
  {
      var l1 = new Dictionary<Guid, BaseEntity>(capacity: 64);
      try
      {
          await HydrateL1Async(workflowIds, l1, ct);
          var l2Writes = await TraverseAndValidateAsync(l1, ct);
          await WriteL2Async(l2Writes, ct);
          return new NoContentResult();
      }
      finally
      {
          l1.Clear();
      }
  }
  ```
- Keep `OrchestrationService` lifetime **Scoped** (consistent with v3.2.0 Phase 9 + v3.2.0 PITFALLS Pitfall 2 on DbContext lifetime — same reasoning).
- Add `tests/BaseApi.Tests/Composition/OrchestrationServiceLifetimeFacts.cs` asserting `services.GetServiceDescriptor(typeof(IOrchestrationService)).Lifetime == ServiceLifetime.Scoped`.
- Add a concurrency fact: spin 8 parallel `StartAsync` calls against disjoint `WorkflowId` sets, assert each traversal sees ONLY its own entities. This catches a regression to Singleton.

**Warning signs (code review):**
- `private readonly Dictionary<Guid, ...> _l1` (or any per-Start state) as an instance field on `OrchestrationService`.
- `services.AddSingleton<IOrchestrationService>(...)`.
- Any helper method that reads from a `static` field of the service.
- `Dictionary<...>` instances stored on `HttpContext.Items` (works, but obscures lifecycle — keep it local).

---

### Pitfall 7: Cleanup-on-throw using finalizers / `IDisposable` instead of `try/finally` — leaks under exception paths

**What goes wrong:**
Developer wraps the L1 dictionary in a `class L1Scope : IDisposable` whose `Dispose` clears the dictionary, and writes:
```csharp
using var scope = new L1Scope();
await HydrateAsync(scope.Dictionary);
await TraverseAsync(scope.Dictionary);   // throws ValidationException for cycle
await WriteL2Async(scope.Dictionary);
```
This LOOKS clean, but if `TraverseAsync` throws and the exception flows through v3.2.0's Phase 4 `IExceptionHandler` chain that catches and maps the exception, `Dispose` still runs — but timing across the async pipeline can be subtle. Worse failure mode: developer uses a finalizer (`~L1Scope`). Finalizers run on the GC finalizer thread, no `AsyncLocal` flow, no `HttpContext`, no correlation ID, unpredictable timing.

**Why it bites (root cause):**
`try/finally` is the .NET-idiomatic way to guarantee cleanup runs in both success and exception paths on the same thread / async context. `using` is sugar over `try/finally` for `IDisposable`. Finalizers are an unmanaged-resource escape hatch, not a domain-cleanup primitive.

For the L1 dictionary specifically, cleanup is "drop the reference" — managed memory; GC handles it. Explicit `Clear()` is purely a hint for large dictionaries to release the internal entry array sooner. No `IDisposable` needed.

**Which phase addresses it:**
**R1 — L1 build pipeline.**

**Prevention strategy (concrete):**
- Use the pattern in Pitfall 6: local variable + `try/finally` + `l1.Clear()` in the finally block.
- Do NOT introduce an `IDisposable` wrapper for the L1 dictionary unless it owns an unmanaged or pooled resource.
- If pooling becomes a need (e.g., large workflow graphs allocate 10k entries per Start), use `System.Buffers.ArrayPool<T>` or `Microsoft.Extensions.ObjectPool` — both have explicit return-on-finally patterns.
- Document in writer XML doc: "Cleanup is via local-variable scope + `try/finally l1.Clear()`. Do not introduce IDisposable; do not use finalizers."

**Warning signs (code review):**
- `class L1Scope : IDisposable` or any helper wrapping L1 in disposable semantics.
- `~L1Scope()` or any finalizer in v3.3.0 source.
- `using var l1 = new L1Container();` — wrong pattern; use `try/finally`.
- Missing `try/finally` in `StartAsync` (cleanup only on success path).

---

### Pitfall 8: Recursive DFS — `StackOverflowException` on deep workflow graphs (uncatchable crash)

**What goes wrong:**
Natural way to write DFS cycle detection:
```csharp
void Visit(Guid stepId, HashSet<Guid> visited, HashSet<Guid> inStack)
{
    if (inStack.Contains(stepId)) throw new CycleException(stepId);
    if (!visited.Add(stepId)) return;
    inStack.Add(stepId);
    foreach (var next in GetNextStepIds(stepId))
        Visit(next, visited, inStack);
    inStack.Remove(stepId);
}
```
On a 5-step workflow, fine. On a 5000-step chain, exceeds the .NET default stack (~1 MB ≈ 10-15k frames Release, half in Debug). Process dies with `StackOverflowException` — **uncatchable**: no `IExceptionHandler` from v3.2.0 Phase 4 sees it; no RFC 7807 response emitted; request hangs until connection times out; OTel may or may not capture the exit depending on timing. Hard crash.

**Why it bites (root cause):**
`StackOverflowException` is one of three .NET exceptions the runtime cannot recover from (others: `OutOfMemoryException`, `ExecutionEngineException`). Once raised, process exits. K8s liveness probe (correctly tagged "self" per v3.2.0 Pitfall 15) doesn't fire; container just dies. Restart loop ensues.

User has no bound on workflow depth (no SQL constraint, no validator). Any user — or any test fixture — can submit a 10k-step chain.

**Which phase addresses it:**
**R2 — Workflow graph traversal.**

**Prevention strategy (concrete):**
- **Always use iterative DFS** with an explicit `Stack<>` (or `List<>` used as a stack). Never write recursive graph traversal in production code regardless of perceived "reasonable" depth:
  ```csharp
  void DetectCycleAndCollectEdges(Guid entry, Dictionary<Guid, StepEntity> steps, List<(Guid parent, Guid child)> edges)
  {
      var visited = new HashSet<Guid>();
      var inPath = new HashSet<Guid>();
      var stack = new Stack<(Guid step, IEnumerator<Guid> children)>();
      stack.Push((entry, steps[entry].NextStepIds.GetEnumerator()));
      inPath.Add(entry);
      while (stack.Count > 0)
      {
          var (current, it) = stack.Peek();
          if (it.MoveNext())
          {
              var child = it.Current;
              if (inPath.Contains(child)) throw new CycleException(current, child);
              edges.Add((current, child));
              if (visited.Add(child))
              {
                  if (!steps.TryGetValue(child, out var childEntity))
                      throw new MissingStepException(current, child);
                  stack.Push((child, childEntity.NextStepIds.GetEnumerator()));
                  inPath.Add(child);
              }
          }
          else
          {
              inPath.Remove(current);
              stack.Pop();
          }
      }
  }
  ```
- White/gray/black coloring encoded as: `visited` (black + gray) vs `inPath` (gray only). Node in `inPath` = back-edge = cycle. Node in `visited` but not `inPath` = shared DAG node — NOT a cycle.
- Add a fact in `tests/BaseApi.Tests/Features/Orchestration/CycleDetectionFacts.cs` that constructs a 50,000-step chain in-memory and asserts traversal completes without StackOverflow.
- Add a fact for shared-DAG-node graphs (A→B, A→C, B→D, C→D) — must NOT be flagged as a cycle. **This is the discriminating test.**

**Warning signs (code review):**
- Any recursive method in `OrchestrationService` whose recursion is over user-supplied graph data (`NextStepIds`, `EntryStepIds`).
- Use of `Visit(...)` / `Dfs(...)` method that calls itself.
- Test fixtures that only use small (5-10 node) graphs — no large-graph fact.

---

### Pitfall 9: Visited-set semantics confused — "shared node in DAG" misclassified as cycle, OR real cycle missed

**What goes wrong:**
Developer uses a single `HashSet<Guid> visited`:
```csharp
void Visit(Guid id) {
    if (!visited.Add(id)) throw new CycleException(id);  // BUG
    foreach (var next in GetNext(id)) Visit(next);
}
```
Rejects the legitimate DAG `A→B, A→C, B→D, C→D` because `D` is visited twice — but it's NOT a cycle, just a shared descendant. 422 fires on a valid workflow.

Mirror bug:
```csharp
void Visit(Guid id) {
    if (visited.Contains(id)) return;  // BUG: returns without distinguishing in-path
    visited.Add(id);
    foreach (var next in GetNext(id)) Visit(next);
}
```
Misses cycles entirely — once in `visited`, never revisited even when currently on active path.

**Why it bites (root cause):**
Cycle detection needs TWO sets with different semantics:
- **`visited`** (black): subtree fully traversed — never re-traverse, NOT a cycle marker.
- **`inPath`** (gray): nodes currently on active DFS path. Back-edge to `inPath` IS a cycle.

A single set conflates the two. Textbook DAG-traversal subtlety, easy to forget under time pressure.

**Which phase addresses it:**
**R2 — Workflow graph traversal.**

**Prevention strategy (concrete):**
- Use the two-set pattern from Pitfall 8. Comment in code:
  ```csharp
  // CYCLE DETECTION INVARIANT:
  //   visited = nodes whose subtree is fully explored ("black")
  //   inPath  = nodes on the current DFS path  ("gray")
  // A child already in inPath = cycle.
  // A child in visited but not in inPath = shared DAG node, NOT a cycle.
  ```
- Test matrix in `CycleDetectionFacts.cs`:
  1. Linear chain (A→B→C) — no cycle.
  2. Self-loop (A→A) — cycle.
  3. Short cycle (A→B→A) — cycle.
  4. Deep cycle (A→B→C→D→B) — cycle.
  5. Diamond DAG (A→B, A→C, B→D, C→D) — no cycle. **Discriminating test.**
  6. Multiple entry points sharing descendants — no cycle.
  7. Cycle in branch unreachable from `EntryStepIds[*]` — silently ignored per current contract; document the choice.
- Error response on cycle (422) must include the offending `(parentStepId, childStepId)` pair — same format as v3.2.0 Phase 4 PostgresExceptionMapper field-name convention.

**Warning signs (code review):**
- Only one `HashSet<Guid>` in the traversal.
- Test suite without a diamond-DAG fact.
- 422 errors on valid DAGs in production reports.

---

### Pitfall 10: JsonSchema.Net schema re-parsed per validation — order-of-magnitude perf hit + GC pressure

**What goes wrong:**
The Payload↔ConfigSchema validation gate (closing v3.2.0's deferred VALID-21) is the perf-critical path of Start — every Assignment requires one schema validation. Naive code:
```csharp
foreach (var assignment in workflowAssignments)
{
    var schemaJson = l1[GetConfigSchemaId(assignment)].Definition;
    var schema = JsonSchema.FromText(schemaJson);            // RE-PARSES every iteration
    var result = schema.Evaluate(JsonNode.Parse(assignment.Payload));
    if (!result.IsValid) throw new ValidationException(...);
}
```
A workflow with 100 assignments referencing 5 distinct ConfigSchemaIds parses the schema 100 times instead of 5. JsonSchema.Net's per-parse cost (draft detection, `$ref` resolution, anchor identification, base-URI propagation) is non-trivial. At 100 assignments × 1ms parse, the endpoint adds 100ms unnecessary latency.

Second order: each parse allocates a `JsonSchema` object graph (constraint tree, options, registry) that immediately becomes garbage. On a hot path, stresses Gen0 GC and contributes to p99 spikes.

**Why it bites (root cause):**
`JsonSchema.FromText` is designed for one-shot parsing. The [json-everything docs](https://docs.json-everything.net/schema/basics/) recommend registering once in a `SchemaRegistry` (or caching the parsed `JsonSchema` in an app-level dictionary) and reusing. The library's 2025 perf work explicitly moved analysis to registration time so reuse is fast and one-shot parsing is the slow path (see [json-everything refactoring blog](https://blog.json-everything.net/posts/refactoring-with-purpose/)).

**Which phase addresses it:**
**R3 — Payload↔ConfigSchema validation gate.**

**Prevention strategy (concrete):**
- Inside `OrchestrationService.StartAsync`, build a per-Start `Dictionary<Guid, JsonSchema>` keyed by `SchemaEntity.Id`, populated lazily:
  ```csharp
  var schemaCache = new Dictionary<Guid, JsonSchema>(capacity: 16);
  JsonSchema GetSchema(Guid schemaId)
  {
      if (!schemaCache.TryGetValue(schemaId, out var s))
      {
          s = JsonSchema.FromText(l1[schemaId].Definition);
          schemaCache[schemaId] = s;
      }
      return s;
  }
  ```
- Cache is **per-Start** (local to `StartAsync`) for v3.3.0. A future milestone may promote to process-wide `IMemoryCache<Guid, JsonSchema>` keyed on `(SchemaId, xmin)` for cross-Start reuse — out of scope here (requires invalidation logic).
- Reuse the same `EvaluationOptions` instance across all validations (it's also heavy to construct — see Pitfall 11).
- **Do not** call `JsonSchema.FromText` more than once per `(SchemaId, Start request)`.
- Perf fact: 1000-assignment workflow → assert `p95 < 500ms` (parallels the v3.2.0 `<500ms` SSRF regression assertion).

**Warning signs (code review):**
- `JsonSchema.FromText(...)` inside a `foreach` over assignments — must lift above or cache.
- Construction of `EvaluationOptions` inside the loop.
- No perf assertion on the validation gate (silent regression hazard).

---

### Pitfall 11: JsonSchema.Net SSRF defense regressed — new validator paths bypass the Phase 8 lockdown

**What goes wrong:**
v3.2.0 Phase 8 explicitly disabled remote `$ref` fetching on the `SchemaEntity.Definition` validator with a `<500ms` regression assertion (PROJECT.md line 187). v3.3.0 adds a SECOND validator (Payload↔ConfigSchema gate) using JsonSchema.Net. If the new validator is constructed with fresh `EvaluationOptions` and the SSRF-disabling configuration is not re-applied, a malicious `Schema.Definition` registered earlier (or maliciously crafted by an authenticated caller in a future auth-enabled milestone) can include `$ref: "http://attacker.example/schema.json"` that the second validator dutifully fetches — exfiltrating internal network topology or pivoting to internal services.

**Why it bites (root cause):**
SSRF defenses live on `EvaluationOptions` (or via global `SchemaRegistry` setup). Each new validator instantiation re-creates options unless they explicitly inherit. The Phase 8 lockdown lives in the SchemaEntity validator's setup; nothing automatically propagates that decision to the new Orchestration-gate validator.

JsonSchema.Net's `EvaluationOptions.AllowReferencesIntoUnknownKeywords` and the registry's `SchemaResolver` configuration are the relevant knobs — both need explicit configuration to refuse network fetches.

**Which phase addresses it:**
**R3 — Payload↔ConfigSchema validation gate.**

**Prevention strategy (concrete):**
- Extract a shared factory in `BaseApi.Core` (or co-located with the existing Schema validator):
  ```csharp
  public static class JsonSchemaConfig
  {
      public static EvaluationOptions DefaultOptions { get; } = new()
      {
          OutputFormat = OutputFormat.List,
          // ... SSRF lockdown: configure SchemaRegistry to refuse non-local resolution
          // per the same approach Phase 8 took for the Schema validator
      };
  }
  ```
- BOTH validators (existing SchemaEntity setup + new Orchestration gate) MUST use `JsonSchemaConfig.DefaultOptions` — not construct their own.
- The v3.2.0 `<500ms` SSRF regression test stays; ADD an equivalent test for the new gate: construct a Payload-validation flow with a malicious `$ref` in the cached schema, assert evaluation completes in `<500ms` and returns a validation error (no network call).
- Document in `JsonSchemaConfig.cs` XML doc: "DO NOT bypass this factory. SSRF defense is in here; bypassing is a security regression."

**Warning signs (code review):**
- Any `new EvaluationOptions()` outside `JsonSchemaConfig.DefaultOptions`.
- Direct `JsonSchema.FromText(...).Evaluate(payload)` without passing options (uses library defaults — explicit is safer).
- Removal or weakening of the Phase 8 `<500ms` SSRF regression assertion.

---

### Pitfall 12: Stop endpoint must SCAN to evict children — O(N) keyspace blocking + cross-test interference

**What goes wrong:**
Stop says "delete all L2 keys created by Start for the given WorkflowIds." For the `{workflowId}` root, deletion is trivial: `db.KeyDeleteAsync(rootKey)`. For the N `{workflowId:stepId}` children, the implementer reaches for `db.KeyDeleteAsync(server.Keys(pattern: $"{workflowId}:*"))`. Under the hood, `IServer.Keys` calls **KEYS** by default on small datasets and **SCAN** on larger ones — Redis-side cost is still O(N) over the entire keyspace, and if multiple test classes share one Redis container (Pitfall 16), Stop in test A latency-spikes test B's writes.

Worse: the `{workflowId:stepId}` chain may follow `nextStepId` through steps NOT in `entryStepIds[]` (PROJECT.md line 19 — chain form, one nextStepId per record). If Stop only deletes keys reachable from `entryStepIds[]`, it leaks the deeper chain. If Stop SCANs by prefix, it picks up everything but blocks Redis.

**Why it bites (root cause):**
Redis SCAN is incremental and non-blocking per-call, but total work is O(N) over the keyspace. KEYS is O(N) AND blocking — every other op queues behind it. Neither is acceptable for routine teardown on shared infrastructure.

The correct pattern depends on whether the writer stores enough metadata for direct deletion:
- **Option A:** `{workflowId}` root stores `allStepIds[]` (not just `entryStepIds[]`) — Stop reads root, deletes each child by exact key, deletes root. O(stepCount) targeted ops; no SCAN.
- **Option B:** Walk the chain at Stop — read root for `entryStepIds[]`, read each entry's `{workflowId:stepId}` to find `nextStepId`, recurse. Same O(stepCount), more round-trips.
- **Option C:** SCAN by prefix — O(keyspace), unacceptable.

Option A is cleanest, but requires the **writer side (Start)** to populate `allStepIds[]` on the root. The milestone roadmap must encode this upfront.

**Which phase addresses it:**
**R4 — L2 Redis writer** (must write `allStepIds[]`) and **R5 — Stop endpoint** (must use it).

**Prevention strategy (concrete):**
- The `{workflowId}` root DTO must include `allStepIds[]` that enumerates every step key written by Start. The existing user-specified shape includes `entryStepIds[]` but NOT `allStepIds[]` — the roadmap needs an explicit decision: (a) extend the root DTO with `allStepIds[]`, OR (b) accept the chain-walk at Stop time. **Recommend (a)** for simplicity and minimum Stop-time RTTs.
- The writer's traversal already enumerates every step (for cycle detection); collecting them into `allStepIds[]` is essentially free.
- Stop implementation:
  ```csharp
  var rootJson = await db.StringGetAsync($"{{{workflowId:N}}}");
  if (rootJson.IsNullOrEmpty)
      return new NoContentResult();  // idempotent: nothing to do
  var root = JsonDeserialize<L2RootDto>(rootJson);
  var batch = db.CreateBatch();
  var deletes = new List<Task>(capacity: root.AllStepIds.Count + 1);
  foreach (var stepId in root.AllStepIds)
      deletes.Add(batch.KeyDeleteAsync($"{{{workflowId:N}:{stepId:N}}}"));
  deletes.Add(batch.KeyDeleteAsync($"{{{workflowId:N}}}"));
  batch.Execute();
  await Task.WhenAll(deletes);
  return new NoContentResult();
  ```
- Stop MUST return 204 even when root doesn't exist — idempotency contract (PROJECT.md line 23).
- **Never** call `server.Keys(pattern: ...)` in v3.3.0 production code. If it appears, it's a bug.

**Warning signs (code review):**
- `IServer.Keys(...)` or `server.KeysAsync(...)` in `OrchestrationService` or any L2-related code.
- `KEYS` command in Lua scripts (same hazard).
- Stop logic that doesn't read the root first — implies SCAN-by-prefix.
- Missing `allStepIds[]` (or equivalent enumeration field) on the `{workflowId}` root DTO.

---

### Pitfall 13: OTel Redis instrumentation enabled → traces emitted with no backend → SDK-side dropped silently OR accidental traces-pipeline revival

**What goes wrong:**
Developer adds `services.AddOpenTelemetry().WithTracing(t => t.AddRedisInstrumentation(...));` — they "want Redis observability." Two failure modes:

1. **No backend, silent drop:** v3.2.0 Phase 11 D-03 explicitly stripped `.WithTracing(...)` from `AddBaseApiObservability` and deleted the collector traces pipeline (PROJECT.md line 115). The new `AddRedisInstrumentation` call re-enables traces collection in the SDK; spans are generated, batched, queued — and dropped at the OTLP exporter (no traces endpoint). Memory pressure (small but real), CPU cost (sampling, attribute serialization), worst case a queue-overflow log. The team sees no Redis spans anywhere and concludes "Redis instrumentation doesn't work" — actually they accidentally turned the traces pipeline back on.

2. **Accidental revival:** A well-meaning PR re-adds `.WithTracing(...)` to `AddBaseApiObservability` AND adds an OTLP traces endpoint to the collector. Phase 11 D-03 reversed without the team noticing. Traces storage costs spike, no sampling tuning, volume catastrophic.

**Why it bites (root cause):**
StackExchange.Redis's instrumentation is tracing-only — it does NOT emit metrics (separate Redis metrics story via `INFO` or server-exposed metrics). Calling `AddRedisInstrumentation` commits to a traces pipeline.

Also: OpenTelemetry.Instrumentation.StackExchangeRedis has a [historical duplicate-span issue](https://github.com/open-telemetry/opentelemetry-dotnet/issues/1301) in some versions where the internal profiler races and emits duplicate spans — another reason to avoid enabling unless required. The instrumentation also has a [known baggage non-propagation bug](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/issues/674) (no logic to copy baggage from parent activity to child Redis activity).

**Which phase addresses it:**
**R6 — Observability extension.**

**Prevention strategy (concrete):**
- **Do NOT add `OpenTelemetry.Instrumentation.StackExchangeRedis` to the project in v3.3.0.** Redis observability rides on the existing logs pipeline: structured `ILogger<OrchestrationService>` events (`redis.l2.write.start`, `redis.l2.write.complete`, `redis.l2.write.error`) flow through MEL → OTel → ES with the v3.2.0 `service.name=sk-api` resource tag intact (PROJECT.md line 114, `resource_to_telemetry_conversion` preserves the label).
- For Redis-specific metrics (write count, write latency), use the .NET 8 `Meter` API directly:
  ```csharp
  private static readonly Meter Meter = new("sk-api.orchestration");
  private static readonly Counter<long> L2Writes = Meter.CreateCounter<long>("orchestration.l2_writes");
  private static readonly Histogram<double> L2WriteLatencyMs = Meter.CreateHistogram<double>("orchestration.l2_write_latency_ms");
  ```
  Flows through v3.2.0 Phase 11 Prometheus pipeline automatically.
- ADD a build guard fact in `tests/BaseApi.Tests/Composition/NoTracesBackendFacts.cs`:
  - Assert no `.csproj` references `OpenTelemetry.Instrumentation.StackExchangeRedis`.
  - Assert `AddBaseApiObservability` does NOT call `.WithTracing(...)`.
  - Regression guard for the Phase 11 D-03 invariant.
- Future milestones may add Redis tracing with a documented decision; v3.3.0 keeps the traces backend dark.

**Warning signs (code review):**
- `<PackageReference Include="OpenTelemetry.Instrumentation.StackExchangeRedis" />` in any `.csproj`.
- `.AddRedisInstrumentation(...)` anywhere.
- `.WithTracing(...)` re-introduced into `AddBaseApiObservability`.
- Collector config file gaining a `traces` pipeline.

---

### Pitfall 14: Redis healthcheck added without tag discipline — `/health/live` flaps when Redis blips (regresses v3.2.0 Phase 5 HEALTH-01)

**What goes wrong:**
Developer adds `services.AddHealthChecks().AddRedis(connStr);` and calls it done. By default, `AddRedis` registers a check with **no tags**. v3.2.0 Phase 5 health-endpoint wiring uses tag-based predicates:
- `/health/live` → `Predicate = c => c.Tags.Contains("live")`
- `/health/ready` → `Predicate = c => c.Tags.Contains("ready")`
- `/health/startup` → `Predicate = c => c.Tags.Contains("startup")`

An untagged check is NOT picked up by any predicate — it appears nowhere. Worse, an inexperienced operator "fixes" this by changing the predicate to `Predicate = _ => true` for `/health/live`. Now any Redis hiccup → 503 on `/health/live` → K8s kills the pod → cascading restart loop. This is **exactly** the failure mode v3.2.0 PITFALLS Pitfall 15 was written to prevent for Postgres; the Redis equivalent is just as dangerous.

**Why it bites (root cause):**
v3.2.0 Phase 5 locked the "live never touches external state" contract. Adding any new dependency requires applying the same tag discipline. The `AddRedis` extension's default of "no tags" makes the wrong configuration the path of least resistance.

**Which phase addresses it:**
**R6 — Observability extension.**

**Prevention strategy (concrete):**
- Redis healthcheck registration MUST explicitly tag `ready` only:
  ```csharp
  services.AddHealthChecks()
      .AddRedis(
          redisConnectionString: cfg.GetConnectionString("Redis")!,
          name: "redis",
          failureStatus: HealthStatus.Unhealthy,
          tags: new[] { "ready" });
  ```
- Extend v3.2.0's existing "live doesn't touch DB" acceptance test:
  - Stop the Redis container.
  - Hit `/health/live` → must return 200.
  - Hit `/health/ready` → must return 503.
  - Hit `/health/startup` → must return 200 (migrations completed before Redis went away).
- Document in v3.3.0 PR that Redis follows Postgres's tag discipline (forward-reference v3.2.0 PITFALLS.md Pitfall 15).
- Avoid `AddRedis(...).AddCheck("redis-ping", ...)` patterns that double-register.

**Warning signs (code review):**
- `AddRedis(...)` without an explicit `tags:` argument.
- Predicate change on `/health/live` (or any mapping with `Predicate = _ => true`).
- New healthcheck endpoint without tag filtering.
- Missing acceptance test for "Redis down → /health/live still 200."

---

### Pitfall 15: X-Correlation-Id scope lost across `IConnectionMultiplexer` async ops — Redis log events have no correlation ID (regresses Phase 11 E2E)

**What goes wrong:**
v3.2.0 Phase 4 set up `CorrelationIdMiddleware` to push `_logger.BeginScope({ CorrelationId = id })` for the request duration. The scope flows via `AsyncLocal<T>`. For Postgres ops issued through `AppDbContext` (which flows through awaits cleanly), the scope is intact in all related log lines. Phase 11 E2E test (`SchemasLogsE2ETests`) round-trips the correlation ID through OTel to ES and asserts it.

Redis ops issued via `db.StringGetAsync(...)` SHOULD also flow the scope (`AsyncLocal` carries through `await` by default), BUT:
- StackExchange.Redis's internal completion handling sometimes runs on threads completed by the network listener. `AsyncLocal` survives `ConfigureAwait(false)` (unlike `SynchronizationContext`), but **only if continuations are awaited normally**. Code that does fire-and-forget (`_ = db.StringSetAsync(...);` without `await`) drops the scope outright.
- Custom test fakes for `IConnectionMultiplexer` (used in tests that don't want real Redis) often spawn their own `Task.Run`, which captures the current `AsyncLocal` snapshot — usually fine, but `Task.Factory.StartNew(... TaskCreationOptions.LongRunning)` preserves the snapshot differently.
- Adding Redis writes inside the Start endpoint must NOT regress the Phase 11 E2E contract — the log line "wrote {workflowId} root to L2" must carry the same correlation ID as the inbound HTTP request.

**Why it bites (root cause):**
`AsyncLocal<T>` flow is fragile under fire-and-forget patterns, custom task schedulers, and `ThreadPool.UnsafeQueueUserWorkItem`. Fix is structural (always `await`), not mechanical. The [OTel baggage parallel-tasks issue #3257](https://github.com/open-telemetry/opentelemetry-dotnet/issues/3257) documents an analogous AsyncLocal sharing surprise in OTel itself.

**Which phase addresses it:**
**R6 — Observability extension** (and **R4 — L2 Redis writer** for the always-await discipline).

**Prevention strategy (concrete):**
- **Always `await` Redis ops.** No `_ = db.StringSetAsync(...);` patterns; no `Task.Run(() => db.StringSetAsync(...))` for "background" writes. The v3.3.0 Start endpoint is synchronous-by-contract: returns 204 ONLY after all L2 writes complete.
- Use `IBatch.Execute() + Task.WhenAll(batchTasks)` for parallel pipelined writes — preserves scope on the awaiter.
- ADD a fact to the existing Phase 11 E2E test (or sibling): submit a Start, force the writer to emit a known log line (`"orchestration.l2.write.complete"`), poll ES for that log line, assert it carries the `CorrelationId` field matching the inbound HTTP `X-Correlation-Id` header.
- Custom `IConnectionMultiplexer` fakes for unit tests must complete tasks on the same execution context — simplest: return `Task.FromResult(...)` synchronously rather than spawning.
- Document in `OrchestrationService.cs`: "All Redis ops must be awaited within the request's execution context. Fire-and-forget Redis ops break correlation-id propagation and the Phase 11 E2E contract."

**Warning signs (code review):**
- `_ = db.SomeAsyncOp();` (fire-and-forget) anywhere in v3.3.0 source.
- `Task.Run(() => db.SomeAsyncOp())` for Redis ops.
- Custom multiplexer fakes that use `Task.Run` internally.
- Phase 11 E2E test passing for Postgres-related log lines but no equivalent assertion for Redis-related log lines.

---

### Pitfall 16: xUnit v3 per-class Redis isolation — `FLUSHDB` on teardown nukes other concurrent classes; `KEYS pattern` blocks shared Redis

**What goes wrong:**
v3.2.0's per-class Postgres pattern (Phase 3 D-15, `stepsdb_test_*` throwaway DBs, SHA-256 baseline snapshot `0d98b0de…0aac127` proving zero leaks, honored 11×) sets a high bar for v3.3.0 Redis isolation. Naive approach: every test class connects to Redis DB 0, runs asserts, calls `db.Execute("FLUSHDB")` in `DisposeAsync`. xUnit v3 `[assembly: AssemblyFixture]` shares one Redis container across the suite (only one Compose service), and xUnit v3 runs collections in parallel by default. Class A's `FLUSHDB` deletes Class B's mid-test data. Random failures.

"Fix" that's worse: each class iterates `IServer.Keys(pattern: $"test_{className}:*")` and deletes — `KEYS` is O(N) over the whole DB and blocks Redis, latency-spiking other concurrent tests.

Second-order: Redis logical DB indices (`SELECT 0..15`) are **not supported in Redis Cluster**. sk_p's v3.3.0 is single-node Redis (per the new `compose.yaml` service), so SELECT works. But documenting the constraint matters for any future Cluster migration.

**Why it bites (root cause):**
v3.2.0's psql `\l` SHA-256 cadence is enforced; equivalent Redis discipline is missing by default. Without explicit prevention, the test-isolation bar for Redis is "0 keys at suite-end" — but the techniques to achieve that (FLUSHDB, KEYS) are themselves anti-patterns.

**Which phase addresses it:**
**R7 — Test fixtures.**

**Prevention strategy (concrete):**
- **Key-prefix isolation, not DB-index isolation, not FLUSHDB.** Each test class generates a unique prefix at fixture-setup:
  ```csharp
  public class OrchestrationStartFixture : IAsyncLifetime
  {
      public string KeyPrefix { get; } = $"test_{Guid.NewGuid():N}_";
      public ConnectionMultiplexer Mux { get; private set; } = default!;
  }
  ```
- The Start/Stop endpoints already read a key shape from the writer config — make the prefix injectable so tests scope themselves.
- Teardown: SCAN (not KEYS) with the prefix:
  ```csharp
  public async Task DisposeAsync()
  {
      var server = Mux.GetServer(Mux.GetEndPoints()[0]);
      await foreach (var key in server.KeysAsync(pattern: $"{KeyPrefix}*", pageSize: 250))
          await db.KeyDeleteAsync(key);
  }
  ```
  `IServer.KeysAsync` uses SCAN under the hood — non-blocking and incremental.
- Add a Redis equivalent of v3.2.0's psql `\l` SHA-256 snapshot for the AssemblyFixture suite-end:
  - BEFORE suite: `redis-cli --scan --pattern 'test_*' | wc -l` → expect 0
  - AFTER suite: same — expect 0
  - Assert equality (analogous to the v3.2.0 Phase 3 D-15 invariant).
- Document in test README: "Redis tests use key-prefix isolation. Never `FLUSHDB`. Never `KEYS *` (use `KeysAsync(pattern)`)."

**Warning signs (code review):**
- `db.Execute("FLUSHDB")` or `db.Execute("FLUSHALL")` in test code.
- `server.Keys(...)` (synchronous, KEYS-backed) in test code.
- Test classes that don't scope keys to a unique prefix.
- Missing BEFORE/AFTER Redis-keyspace assertion in the suite-cleanup fixture.

---

## Moderate Pitfalls

### Pitfall 17: Mapperly used for entity → bytes serialization — wrong tool for the job

**What goes wrong:**
Developer reads "Mapperly is for DTO↔DTO source-gen mapping" and tries to extend it to serialize the L2 DTO to a `byte[]` for Redis:
```csharp
[Mapper]
public static partial class L2WorkflowMapper
{
    public static partial byte[] ToRedisBytes(L2WorkflowRootDto dto);  // WRONG
}
```
Build fails with Mapperly diagnostics (`RMG020` or similar — no obvious mapping from object → bytes). Developer adds workaround attributes, fights the generator, eventually inlines `System.Text.Json` calls — but now there's a half-built mapper class polluting the codebase.

**Why it bites (root cause):**
Mapperly is a property-to-property mapper. Serialization (object → bytes / bytes → object) is a distinct concern. v3.2.0's Mapperly setup (RMG007/RMG012/RMG020/RMG089 promoted to errors per PROJECT.md line 83) is configured for DTO↔DTO and produces noisy build errors for anything else.

**Which phase addresses it:**
**R4 — L2 Redis writer.**

**Prevention strategy (concrete):**
- Two-step pipeline: EntityFromL1 → L2DTO (Mapperly), then L2DTO → `string` (System.Text.Json):
  ```csharp
  var l2Dto = L2WorkflowMapper.ToL2RootDto(workflowEntity, entryStepIds, allStepIds, cron, jobId, liveness);
  var json = JsonSerializer.Serialize(l2Dto, JsonOptions);
  await db.StringSetAsync(key, json);
  ```
- JSON-as-string is canonical for the v3.3.0 contract (readability + external-consumer interop).
- Share `JsonSerializerOptions` as a `static readonly` field (see Pitfall 18).

**Warning signs (code review):**
- Mapperly `[Mapper]` classes with non-object return types (bytes, strings, RedisValue).
- Mapperly methods accepting `string` or `byte[]` as input.
- Linter warnings RMG020 / RMG089 spiking on the new L2 mapper.

---

### Pitfall 18: `JsonSerializerOptions` constructed per-call — measurable allocation + perf hit

**What goes wrong:**
Developer writes `JsonSerializer.Serialize(dto, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });` inside the L2 write loop. Every call allocates `JsonSerializerOptions` and (worse) triggers re-derivation of the per-type converter metadata cache. The perf cost is real and is a standard .NET 8 perf-review finding.

**Why it bites (root cause):**
`JsonSerializerOptions` is documented as "expensive to create; safe to share." Each new instance triggers metadata derivation; sharing one instance amortizes across all calls.

**Which phase addresses it:**
**R4 — L2 Redis writer.**

**Prevention strategy (concrete):**
- One `static readonly JsonSerializerOptions L2Json` per relevant assembly, configured once:
  ```csharp
  public static readonly JsonSerializerOptions L2Json = new(JsonSerializerDefaults.Web)
  {
      Converters = { new JsonStringEnumConverter() },  // see Pitfall 28
  };
  ```
- If using source-generated `JsonSerializerContext` (recommended for AOT + perf), same advice: one context instance per assembly.

**Warning signs (code review):**
- `new JsonSerializerOptions { ... }` inside a method body (not a `static readonly` field).
- Inconsistent naming/casing across L2 writes (sign of multiple ad-hoc options).

---

### Pitfall 19: Validation order silently violated — DB load happens before existence check, or L2 write happens before validation

**What goes wrong:**
PROJECT.md line 30 locks the validation order: **existence → cycles → schema-edge compatibility → Payload↔ConfigSchema → L1 build → L2 write → cleanup**. A refactor that "moves L2 writes earlier for parallelism" or "lazy-loads L1 inside the traversal" silently violates this contract:
- L2 write before Payload validation → bad-payload Start corrupts L2 with stale entries that Stop must clean up.
- L1 lazy-loaded during traversal → existence check never fires for entities not reached (e.g., orphaned Assignment whose StepId points to a missing step) — silent acceptance of invalid state.

**Why it bites (root cause):**
Order is a contract that's invisible at the type level. Without enforcement (tests + comments), it drifts under perf or refactoring pressure.

**Which phase addresses it:**
**R1 — L1 build pipeline** (orchestrate the order) and across **R2/R3/R4**.

**Prevention strategy (concrete):**
- `StartAsync` is a linear sequence of named methods, in this order, no branching:
  ```csharp
  await VerifyExistenceAsync(workflowIds, l1, ct);   // 1
  await DetectCyclesAsync(l1, ct);                    // 2
  await VerifySchemaEdgesAsync(l1, ct);               // 3
  await ValidatePayloadConformanceAsync(l1, ct);      // 4
  // L1 is fully built by step 1; nothing else builds L1
  await WriteL2Async(l1, ct);                         // 5+6
  // implicit cleanup in finally
  ```
- Each method takes the full `l1` dictionary (already populated) — none populate it lazily.
- Add ONE xUnit fact per validation step that fails at that step and asserts L2 is NOT written. Cleanest assertion: count Redis keys with the test prefix before-and-after; must be equal on failure.
- Comment in `StartAsync`: "VALIDATION ORDER IS A CONTRACT. Do not reorder; do not add work outside this sequence; see .planning/research/PITFALLS.md Pitfall 19."

**Warning signs (code review):**
- `WriteL2Async` called outside `StartAsync` or before all validation methods.
- Lazy population of `l1` (e.g., `l1.TryGetValue(id, out var ent) ?? await db.Set<>().FindAsync(id)`).
- Any branching that conditionally skips a validation step.

---

### Pitfall 20: Idempotent-Start race window narrative undocumented — operators see "impossible" inconsistencies and chase ghosts

**What goes wrong:**
Two concurrent Start requests for the same `[WorkflowId]` interleave their per-key writes. Reader between the writes sees:
- `{workflowId}` root = Start2's data (entryStepIds = [B, C])
- `{workflowId:A}` child = Start1's data (predates A being removed from entryStepIds)
- `{workflowId:B}` child = Start2's data

Reader reasonably concludes "the system is in a broken state" and files a P0 incident. On-call engineer debugs for hours, discovers the interleave, and is told "yes, that's accepted behavior" — frustration ensues.

**Why it bites (root cause):**
The accepted last-write-wins-no-lock semantics has real visible consequences. Without explicit documentation of WHAT inconsistencies a reader can see, every observed inconsistency looks like a new bug.

**Which phase addresses it:**
**R4 — L2 Redis writer.**

**Prevention strategy (concrete):**
- Add a section to v3.3.0 `REQUIREMENTS.md` (or a separate `ORCHESTRATION-SEMANTICS.md`) titled "L2 Consistency Model":
  - Per-key writes are atomic (Redis single-threaded property).
  - Cross-key writes are NOT atomic; concurrent Starts may interleave.
  - **Accepted reader-visible inconsistencies:** (a) `{workflowId}` root reflects Start2 while one or more `{workflowId:stepId}` children reflect Start1; (b) `{workflowId:stepId}` for a stepId no longer in entryStepIds may briefly exist after Start2 if Start1 was still mid-write.
  - **NOT accepted (would be bugs):** (a) `{workflowId}` root exists but ALL `{workflowId:stepId}` children are missing (single-Start atomicity violation — must use Pitfall 5's stage-then-RENAME to prevent); (b) half-written values (impossible because per-key writes are atomic — document the invariant).
- Operators get a runbook entry: "If you see a `{workflowId}` root pointing at stepIds that don't have child keys, check Redis write logs for two overlapping Start requests with the same WorkflowId — accepted interleave."
- Add a stress test: run 50 concurrent Starts on the same WorkflowId; assert (a) eventually consistent within N seconds; (b) no half-written or corrupt values at any point.

**Warning signs (code review):**
- No documentation of the consistency model in milestone artifacts.
- A "fix" PR that adds a Redis lock around Start — contradicts the accepted contract; discuss before merging.
- Stop logic assuming root and children are always consistent (fails under interleaved Start).

---

### Pitfall 21: Cancellation tokens not propagated to Redis ops — request cancel doesn't stop in-flight writes

**What goes wrong:**
Client cancels Start (closes connection, Ctrl+C, K8s shutdown). ASP.NET Core propagates `CancellationToken` to controller → service. Postgres reads via EF Core honor the token. But `IDatabase.StringSetAsync(key, value)` does NOT accept a `CancellationToken` in standard overloads — the operation continues to completion regardless. For a workflow with 1000 children, the cancelled request still writes all 1000 keys.

**Why it bites (root cause):**
StackExchange.Redis's API predates `CancellationToken` ubiquity. Some newer overloads accept tokens (or use `CommandFlags.FireAndForget`), but most don't. The multiplexer batches and pipelines commands; cancellation mid-batch is complex and not exposed in the standard API.

**Which phase addresses it:**
**R4 — L2 Redis writer.**

**Prevention strategy (concrete):**
- Check the token BEFORE each major chunk of work, not inside individual Redis calls:
  ```csharp
  ct.ThrowIfCancellationRequested();
  var batch = db.CreateBatch();
  var tasks = childWrites.Select(w => batch.StringSetAsync(w.Key, w.Value)).ToArray();
  batch.Execute();
  await Task.WhenAll(tasks).WaitAsync(ct);  // .WaitAsync(ct) IS cancellable at the awaiter
  ```
- `.WaitAsync(CancellationToken)` is a .NET 8 `Task` extension that completes early on cancel even if the underlying Redis op continues — in-flight writes complete eventually, but the request returns early.
- Document that a cancelled Start may leave a PARTIAL L2 projection. A subsequent Start (idempotent) overwrites; a subsequent Stop (idempotent) cleans up.
- For Stop specifically, same applies: cancellation early-returns; next Stop is idempotent and finishes cleanup.

**Warning signs (code review):**
- No `ct.ThrowIfCancellationRequested()` calls in `StartAsync` or `WriteL2Async`.
- `await Task.WhenAll(...)` without `.WaitAsync(ct)`.
- Documentation promising "Start is fully cancellable" — it isn't, due to library design.

---

### Pitfall 22: Deeply recursive JSON payload causes uncatchable StackOverflow during JsonSchema evaluation

**What goes wrong:**
A `Schema.Definition` is legitimately recursive (describes a tree-shaped type):
```json
{
  "$defs": {
    "Node": { "type": "object", "properties": { "child": { "$ref": "#/$defs/Node" } } }
  },
  "$ref": "#/$defs/Node"
}
```
v3.2.0 SchemaEntity validator already validates Schema.Definition AS valid; new failure mode is at evaluation time of a deep PAYLOAD against a recursive schema. JsonSchema.Net is generally well-engineered, but version drift or future-milestone schema evolution can introduce stack-blowing code paths.

**Why it bites (root cause):**
Same hazard as Pitfall 8 — StackOverflow is uncatchable. Defense in depth: bound payload depth before invoking the validator.

**Which phase addresses it:**
**R3 — Payload↔ConfigSchema validation gate.**

**Prevention strategy (concrete):**
- Bound payload depth in the validator BEFORE calling `schema.Evaluate(...)`:
  ```csharp
  static int MaxJsonDepth(JsonNode? node, int currentDepth = 0)
  {
      if (currentDepth > 100) return int.MaxValue;  // short-circuit, bound depth
      // ... iterative walk of node tree, return max depth observed
  }
  ```
  Reject payloads with depth > 100 (or whatever the project's policy is) with 422.
- Pin JsonSchema.Net to a known-good version range; do not auto-upgrade across major versions.
- Add a fact constructing a recursive schema + 50-deep payload, assert evaluation completes (proves current library handles it). Add another with a 500-deep payload, assert rejected with 422 due to depth limit.

**Warning signs (code review):**
- Schema.Definition with `$ref` cycles not flagged for review.
- No depth limit on payload validation.
- Recent JsonSchema.Net upgrade without re-running the recursive-schema fact.

---

### Pitfall 23: `IDatabase` cached at construction — stale handle across Redis failover (low risk in v3.3.0, set the discipline now)

**What goes wrong:**
Developer "optimizes" by caching `IDatabase` at construction:
```csharp
public class L2Writer
{
    private readonly IDatabase _db;
    public L2Writer(IConnectionMultiplexer mux) => _db = mux.GetDatabase();  // cached
}
```
Under steady-state, fine. During Redis failover (cluster reconfiguration, primary swap), the multiplexer may internally rebind endpoints. Cached `IDatabase` behavior across failover is library-specific; documented best practice is to call `mux.GetDatabase()` per operation (or per method entry).

**Why it bites (root cause):**
`IDatabase` is documented as cheap to create — canonical pattern is per-use. Caching introduces tiny risk for tiny benefit. v3.3.0 isn't running Cluster, so this is moderate (not critical) — but it's a low-cost discipline to set early.

**Which phase addresses it:**
**R4 — L2 Redis writer.**

**Prevention strategy (concrete):**
- Resolve `IDatabase` per-method, not per-construction:
  ```csharp
  public class L2Writer
  {
      private readonly IConnectionMultiplexer _mux;
      public L2Writer(IConnectionMultiplexer mux) => _mux = mux;
      public Task WriteRootAsync(...) {
          var db = _mux.GetDatabase();
          return db.StringSetAsync(...);
      }
  }
  ```
- Cost is negligible; safety against future-failover regression is worth it.

**Warning signs (code review):**
- `IDatabase` stored as a class field on long-lived services.
- `IDatabase` injected via DI (it's not designed to be — inject the multiplexer).

---

## Minor Pitfalls

### Pitfall 24: `IConnectionMultiplexer` fakes in tests don't implement the full surface — silent NREs at runtime

**What goes wrong:**
Test author hand-rolls an `IConnectionMultiplexer` fake to avoid spinning up Redis. The fake throws `NotImplementedException` on `GetSubscriber()`, `RegisterProfiler()`, or other rarely-used members. v3.3.0's writer doesn't call those, so tests pass. A future PR adds `Subscribe(...)` for some new feature, unit tests pass (don't exercise the new path), but runtime NREs.

**Why it bites (root cause):**
`IConnectionMultiplexer` has a large surface; hand-rolled fakes are always incomplete.

**Which phase addresses it:**
**R7 — Test fixtures.**

**Prevention strategy (concrete):**
- **Don't fake `IConnectionMultiplexer`. Use real Redis** (the Compose service, or a Testcontainer in CI). sk_p already pays for a real Postgres in tests; adding real Redis is the same posture.
- If a fake is unavoidable (truly unit-level test), use NSubstitute/Moq in strict mode so unmocked members fail loudly.

**Warning signs (code review):**
- Hand-rolled `class FakeMultiplexer : IConnectionMultiplexer` with throw-NotImplemented members.
- Unit tests for Redis-dependent code that don't spin up Redis.

---

### Pitfall 25: Logical DB index (`SELECT 0..15`) used for test isolation — documented Cluster incompatibility

**What goes wrong:**
Test author isolates each class to a different Redis logical DB (`opts.DefaultDatabase = 5;`). Works on single-node Redis. Future infra migration to Cluster fails because Cluster does not support `SELECT` (everything is DB 0). Fix is a test refactor that should have been the original design (key-prefix isolation per Pitfall 16).

**Why it bites (root cause):**
Logical DBs are an older Redis feature widely deprecated in modern guidance; Cluster removes them entirely.

**Which phase addresses it:**
**R7 — Test fixtures.**

**Prevention strategy (concrete):**
- Use key-prefix isolation (Pitfall 16). Document: "Do not use Redis logical DBs for isolation; use key prefixes. Cluster compatibility is a future invariant."
- If a developer asks "why not just use DB 1 for tests?" — point at this pitfall.

**Warning signs (code review):**
- `opts.DefaultDatabase = N;` for any N > 0.
- `db.Execute("SELECT", "N")` calls.

---

### Pitfall 26: `RedisValue.HasValue` vs `RedisValue.IsNullOrEmpty` confused — false negatives on empty-string writes

**What goes wrong:**
Writer SETs a key to `""` (intentionally, maybe a stub). Stop reads back with `IsNullOrEmpty` check, sees `true`, returns 204 with "nothing to do" — but the key DOES exist with an empty value, and Stop should have deleted it.

**Why it bites (root cause):**
`RedisValue.IsNullOrEmpty` returns `true` for both "key doesn't exist" and "key exists with empty value." `RedisValue.HasValue` is the inverse but has the same conflation. To distinguish, use `db.KeyExistsAsync(key)` separately.

**Which phase addresses it:**
**R5 — Stop endpoint.**

**Prevention strategy (concrete):**
- Stop uses `db.KeyExistsAsync(rootKey)` (not `IsNullOrEmpty` on a GET) to decide short-circuit:
  ```csharp
  if (!await db.KeyExistsAsync(rootKey)) return new NoContentResult();
  ```
- L2 root DTO contract should never serialize to empty string — but defending costs one extra round-trip.

**Warning signs (code review):**
- Stop logic that checks `IsNullOrEmpty` on a GET to decide existence.
- Writer code that SETs a key to `""` (suspect — verify intent).

---

### Pitfall 27: `Guid` formatting drift in Redis keys — `D` vs `N` vs `B` format produces different keys

**What goes wrong:**
Writer formats `workflowId` with `Guid.ToString("D")` (`6f9619ff-8b86-d011-b42d-00c04fc964ff`). Reader formats with `Guid.ToString("N")` (`6f9619ff8b86d011b42d00c04fc964ff`). Same Guid; different Redis keys. Reader sees nothing.

v3.2.0 locked the convention: `X-Correlation-Id` uses `Guid.NewGuid().ToString("N")` per Phase 4. Redis keys must follow the same convention.

**Which phase addresses it:**
**R4 — L2 Redis writer.**

**Prevention strategy (concrete):**
- All Guid→string in Redis key construction uses `.ToString("N")`. Lock in a helper:
  ```csharp
  internal static class L2KeyShape
  {
      public static string Root(Guid workflowId) => $"{{{workflowId:N}}}";
      public static string Child(Guid workflowId, Guid stepId) => $"{{{workflowId:N}:{stepId:N}}}";
      public static string Processor(Guid processorId) => $"{{{processorId:N}}}";
  }
  ```
- All key construction goes through `L2KeyShape`. Code review fails any inline string interpolation that builds a key.
- Add a fact: write with one format, read with the helper, assert key is found.

**Warning signs (code review):**
- `$"{workflowId}:..."` (uses default "D" format) anywhere.
- Mixed Guid formats across writer and reader.

---

### Pitfall 28: `entryCondition` enum serialized as int — external consumer parses wrong

**What goes wrong:**
L2 child DTO carries `entryCondition` from existing `StepEntity.EntryCondition` enum (PreviousProcessing/Completed/Failed/Cancelled/Always/Never per PROJECT.md line 27). System.Text.Json default serializes enums as int values. External consumers reading L2 see `"entryCondition": 0` and have to know the underlying enum order — fragile and version-coupled.

**Which phase addresses it:**
**R4 — L2 Redis writer.**

**Prevention strategy (concrete):**
- Use `JsonStringEnumConverter` so the value serializes as the enum name (`"PreviousProcessing"`). Configure on the shared options (Pitfall 18):
  ```csharp
  public static readonly JsonSerializerOptions L2Json = new(JsonSerializerDefaults.Web)
  {
      Converters = { new JsonStringEnumConverter() },
  };
  ```
- Document in the L2 DTO XML doc: "enum fields serialized by name, not int."

**Warning signs (code review):**
- L2 DTOs with enum-typed fields and no `JsonStringEnumConverter` in options.
- External-consumer integration tests reading Redis and asserting int values.

---

### Pitfall 29: 422 error responses don't include offending IDs — debugging hostile

**What goes wrong:**
PROJECT.md line 15: "missing next-step ids (422)" — implementation throws `ValidationException("Missing next step id")` with no IDs in the message. RFC 7807 response shows generic text. User can't debug.

**Which phase addresses it:**
**R2 — Workflow graph traversal.**

**Prevention strategy (concrete):**
- All cycle / missing-step / schema-edge / payload-validation errors include offending IDs in `ProblemDetails.Extensions`:
  ```csharp
  throw new OrchestrationValidationException("missing_next_step", new
  {
      ParentStepId = current,
      MissingNextStepId = child,
  });
  ```
- v3.2.0 Phase 4's exception handler maps to RFC 7807; extend to populate `Extensions` from this shape.
- Test matrix in `ValidationFacts.cs`: one fact per failure mode (cycle, missing step, schema mismatch, payload mismatch), assert `Extensions` includes the relevant IDs.

**Warning signs (code review):**
- Validation exceptions thrown with bare strings, no structured payload.
- 422 responses without `Extensions` data.

---

### Pitfall 30: L2 DTO field-shape drift — `inputDefinition` vs `definitionInput` regression

**What goes wrong:**
PROJECT.md line 33 locks: "L2 DTO field names: `inputDefinition` / `outputDefinition` (NOT `definitionIn` / `definitionOut` or `definitionInput` / `definitionOutput`)." A PR adds the writer with `definitionInput` — build passes (it's a string DTO field), tests pass (test uses the writer's own DTO), but external consumers break.

**Which phase addresses it:**
**R4 — L2 Redis writer.**

**Prevention strategy (concrete):**
- Lock field names with a contract fact:
  ```csharp
  [Fact]
  public void L2ProcessorDto_field_names_match_contract()
  {
      var props = typeof(L2ProcessorDto).GetProperties().Select(p => p.Name).ToHashSet();
      Assert.Contains("InputDefinition", props);
      Assert.Contains("OutputDefinition", props);
      Assert.DoesNotContain("DefinitionInput", props);
      Assert.DoesNotContain("DefinitionOutput", props);
  }
  ```
- Contract test that serializes the DTO and asserts JSON keys (`"inputDefinition"`, `"outputDefinition"`).
- Similar fact for the `liveness` shape: must have `timestamp`, `interval`, `status`.

**Warning signs (code review):**
- Field name discussions in PR comments (drift signal).
- DTO rename PRs without corresponding contract-test update.

---

### Pitfall 31: `StartupCompletionService` doesn't probe Redis — startup probe passes before Redis is ready

**What goes wrong:**
v3.2.0 Phase 8 wired `StartupCompletionService.ExecuteAsync` to run `MigrateAsync` and mark startup ready. v3.3.0 adds Redis as a runtime dep but doesn't add a "Redis is reachable" check. Startup probe goes healthy before Redis is up; first Start request blows up; K8s already routed traffic in.

**Why it bites (root cause):**
v3.2.0's startup gate model: "do all critical init synchronously, then mark ready." Redis is now critical (Start endpoint fails without it).

**Which phase addresses it:**
**R6 — Observability extension** (specifically the startup-completion-service extension).

**Prevention strategy (concrete):**
- Extend `StartupCompletionService.ExecuteAsync` to PING Redis (or check `mux.IsConnected`) AFTER migrations and BEFORE marking startup ready:
  ```csharp
  await dbContext.Database.MigrateAsync(ct);
  var mux = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
  // wait up to N seconds for initial connect (AbortOnConnectFail=false means async retry in background)
  var sw = Stopwatch.StartNew();
  while (!mux.IsConnected && sw.Elapsed < TimeSpan.FromSeconds(30))
      await Task.Delay(500, ct);
  if (!mux.IsConnected)
      _logger.LogCritical("startup.redis.not_ready");  // same try/catch/no-rethrow contract as migration (PERSIST-10)
  _startupGate.MarkReady();
  ```
- Same v3.2.0 contract: try / catch / LogCritical / NO rethrow (matches Phase 5 IStartupGate + Phase 8 PERSIST-10).
- Add a fact: start with Redis down, assert `/health/startup` returns 503 for the wait period.

**Warning signs (code review):**
- `StartupCompletionService` modifications that add Redis init but rethrow on failure (violates v3.2.0 contract).
- Missing startup-probe coverage for Redis.

---

### Pitfall 32: Compose service ordering — API container starts before Redis is ready (mirror of v3.2.0 PITFALLS Pitfall 24)

**What goes wrong:**
Mirror of v3.2.0 PITFALLS.md Pitfall 24 (Compose `depends_on` without `condition: service_healthy`), but for Redis. The new Redis service needs a `healthcheck` and the `api` service's `depends_on` needs the condition.

**Which phase addresses it:**
**R0 — Redis infrastructure.**

**Prevention strategy (concrete):**
- Redis Compose service:
  ```yaml
  redis:
    image: redis:7-alpine
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 5s
      timeout: 3s
      retries: 10
      start_period: 5s
    ports:
      - "6380:6379"   # host port 6380 avoids clashes (mirrors v3.2.0 Pitfall 25's Postgres 5433:5432 pattern)
  ```
- API service:
  ```yaml
  api:
    depends_on:
      postgres:
        condition: service_healthy
      redis:
        condition: service_healthy
  ```
- Pin the Redis image tag (`redis:7.4-alpine` not `redis:latest`).

**Warning signs (code review):**
- New `redis:` service without `healthcheck:`.
- `api.depends_on: [redis]` (old syntax) without `condition: service_healthy`.

---

## Phase-Specific Warnings

| Phase | Likely Pitfall(s) | Mitigation Summary |
|-------|-------------------|--------------------|
| R0 — Redis infrastructure | 1, 2, 3, 32 | DI lifetime fact + AbortOnConnectFail explicit + TLS env matrix + Compose healthcheck |
| R1 — L1 build pipeline | 6, 7, 19 | Local-variable L1 + try/finally + ordered StartAsync sequence |
| R2 — Workflow graph traversal | 8, 9, 29 | Iterative DFS + two-set cycle detection + structured-error Extensions |
| R3 — Payload↔ConfigSchema validation gate | 10, 11, 22 | Per-Start schema cache + shared SSRF-locked options + payload depth bound |
| R4 — L2 Redis writer | 4, 5, 15, 17, 18, 20, 21, 23, 27, 28, 30 | No MULTI/EXEC + stage-then-RENAME + always-await + Mapperly→DTO/STJ→bytes split + shared JsonSerializerOptions + consistency model doc + .WaitAsync(ct) + per-call IDatabase + ToString("N") + JsonStringEnumConverter + field-name contract fact |
| R5 — Stop endpoint | 12, 21, 26 | allStepIds[] on root + cancellation tokens + KeyExistsAsync over IsNullOrEmpty |
| R6 — Observability extension | 13, 14, 15, 31 | No-traces-backend regression fact + Redis healthcheck tagged "ready" + correlation-id E2E extended for Redis + StartupCompletionService Redis probe |
| R7 — Test fixtures | 16, 24, 25 | Key-prefix isolation + real Redis (no hand-rolled fakes) + no DB-index isolation |

## Integration with v3.2.0 Disciplines (Regression Guards)

Each of the following v3.2.0 invariants is at risk in v3.3.0; the corresponding pitfall above is the regression guard:

| v3.2.0 Invariant | Risk in v3.3.0 | Guard |
|------------------|-----------------|-------|
| 142/142 GREEN × 3 cadence (Phase 3 D-18) | New Redis dep may flake | Pitfall 16 (key-prefix isolation), Pitfall 24 (real Redis in tests) |
| Per-class throwaway Postgres + SHA-256 snapshot (Phase 3 D-15) | Equivalent needed for Redis | Pitfall 16 (BEFORE/AFTER Redis-keyspace snapshot) |
| TreatWarningsAsErrors + Mapperly RMG codes (Phase 6) | New L2 mapper may trigger RMG020 | Pitfall 17 (DTO/STJ split — don't push Mapperly past its scope) |
| Phase 11 D-03: NO traces backend | OTel Redis instrumentation re-enables traces | Pitfall 13 (build-guard fact in NoTracesBackendFacts) |
| Phase 5 HEALTH-01: live never touches DB | AddRedis defaults to no tags → operator drift | Pitfall 14 (explicit tags + Redis-down E2E fact) |
| X-Correlation-Id end-to-end through OTel to ES (Phase 4 + Phase 11 E2E) | Redis async ops drop AsyncLocal | Pitfall 15 (always-await + extended E2E fact) |
| StartupCompletionService LogCritical/no-rethrow contract (Phase 5 + Phase 8 PERSIST-10) | Redis startup probe must follow same contract | Pitfall 31 (same try/catch/no-rethrow shape) |
| RFC 7807 with offending field names (Phase 4 PostgresExceptionMapper) | New 422 paths must populate Extensions | Pitfall 29 (structured-error fact per failure mode) |
| JSON Schema draft 2020-12 + SSRF disabled (Phase 8) | New Payload-validation gate may bypass SSRF lockdown | Pitfall 11 (shared JsonSchemaConfig factory + extended SSRF regression test) |
| L2 DTO field names (PROJECT.md line 33) | Drift to definitionInput / definitionIn | Pitfall 30 (contract fact on JSON keys) |

## Sources

- StackExchange.Redis Basic Usage (singleton multiplexer): https://stackexchange.github.io/StackExchange.Redis/Basics.html
- StackExchange.Redis Transactions docs (MULTI/EXEC gotchas): https://github.com/StackExchange/StackExchange.Redis/blob/main/docs/Transactions.md
- StackExchange.Redis Issue #2537 (DI guidance + connection multiplexing): https://github.com/StackExchange/StackExchange.Redis/issues/2537
- StackExchange.Redis Issue #1169 (AbortOnConnectFail on Azure): https://github.com/StackExchange/StackExchange.Redis/issues/1169
- StackExchange.Redis Issue #885 (optimistic locking + WATCH): https://github.com/StackExchange/StackExchange.Redis/issues/885
- Redis RENAME canonical docs (atomic key replacement): https://redis.io/docs/latest/commands/rename/
- Redis antirez Atomic Update Patterns: https://redis.antirez.com/fundamental/atomic-updates.html
- Redis SCAN vs KEYS production guidance: https://redis.io/blog/faster-keys-and-scan-optimized/
- Redis Anti-Patterns (incl. KEYS in production): https://redis.io/tutorials/redis-anti-patterns-every-developer-should-avoid/
- Redis Transactions (MULTI/EXEC/WATCH semantics): https://redis.io/docs/latest/develop/using-commands/transactions/
- JsonSchema.Net SchemaRegistry docs: https://docs.json-everything.net/api/JsonSchema.Net/SchemaRegistry/
- JsonSchema.Net release notes (2025 perf work — analysis at registration time): https://docs.json-everything.net/rn-json-schema/
- json-everything blog (refactoring with purpose — perf + registry): https://blog.json-everything.net/posts/refactoring-with-purpose/
- OpenTelemetry.Instrumentation.StackExchangeRedis README (tracing-only nature): https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/main/src/OpenTelemetry.Instrumentation.StackExchangeRedis/README.md
- OpenTelemetry.Instrumentation.StackExchangeRedis duplicate-spans issue #1301: https://github.com/open-telemetry/opentelemetry-dotnet/issues/1301
- OpenTelemetry Redis baggage non-propagation issue #674: https://github.com/open-telemetry/opentelemetry-dotnet-contrib/issues/674
- OpenTelemetry Baggage parallel-tasks issue #3257: https://github.com/open-telemetry/opentelemetry-dotnet/issues/3257
- AspNetCore.HealthChecks.Redis (tags guidance): https://www.nuget.org/packages/AspNetCore.HealthChecks.Redis/
- ASP.NET Core Health Checks (liveness/readiness separation, anti-flap): https://daily-devops.net/posts/health-checks-operational-monitoring/
- Iterative DFS production guidance: https://www.lodely.com/blog/dfs-iterative-vs-recursive
- sk_p `.planning/PROJECT.md` (v3.2.0 invariants + v3.3.0 contract): local
- sk_p prior `.planning/research/PITFALLS.md` (v3.2.0 — 39 pitfalls already mitigated): local (now superseded by this v3.3.0-focused file; v3.2.0 mitigations are encoded in the v3.2.0 phase implementations)

---
*Pitfalls research for: v3.3.0 (Orchestration L3 → L1 → L2 Build Pipeline) — delta on top of v3.2.0 (Steps API MVP)*
*Researched: 2026-05-28*
