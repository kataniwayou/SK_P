# Deferred Items — quick task 260615-kgz

## Out-of-scope (pre-existing, environmental)

- **`--filter-not-trait "Category=RealStack"` is silently ignored by this MTP suite.** Running
  `dotnet test ... -- --filter-not-trait "Category=RealStack"` executes the FULL 757-test suite
  (the trait-exclusion flag form does not take effect under Microsoft.Testing.Platform here), so
  272 live-stack integration tests run against absent infra and fail with `SocketException` /
  `NpgsqlException` (Postgres 127.0.0.1:5433) / `RedisConnectionException` (Redis). These are NOT
  caused by this task's changes — they require the live docker stack (HERMETIC ONLY task).
  Hermetic verification was done via targeted `--filter-class` per task; all 35 edited-class facts
  pass. The orchestrator's live 7-scenario sweep covers the RealStack path separately.
