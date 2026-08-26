# Context Economics, Engineering Graph, Services, and Data Performance

## Bounded context

`ContextResolver` reads the current Goal and Task by exact key, then follows at most 256 outgoing Task dependency edges. It never enumerates all history, checkpoints, evidence, or repository files. Packages are bounded by both item count and UTF-8 bytes, record their exact State read count, and fingerprint only selected facts. Archived and unrelated history therefore does not change hot-path reads or default context bytes.

`hiker resume` is a separate bounded view for explicit project resumption. It reads indexed current kinds with fixed limits and reports the current goal, open/blocked tasks, decisions, valid/stale evidence, worktrees, leases, and a deterministic next action. It is not a conversation replay.

## Intent / dependency / evidence graph

The graph uses typed `dependency_edges`, not token or keyword matching. An impact seed walks incoming consumer edges with explicit node and depth budgets. Strong relations such as `DEPENDS_ON`, `CONSUMES`, `VALIDATES`, and `PROVEN_BY` propagate affected state. Ambiguous relations become `REQUIRES_REVIEW`. Evidence invalidation is permitted only when traversal completed without truncation.

## Service topology and resource leases

Services register runtime/version/build facts, endpoints, datastores, queues, contracts, consumers, and service dependencies as State entities plus typed graph edges. This is runtime-neutral and can represent .NET, Node, Laravel/PHP, Java, Go, Python, Rust, Unity, Qt, or mixed systems without changing the State schema.

Runtime profiles separately retain package manager, build/test/start/debug commands, healthcheck, environments and dependencies. Scripts record purpose, runtime, inputs, outputs, side effects, destructive risk and rollback. Middleware records category, version, consumers, criticality, configuration source, health and failure behavior. The minimum runtime resolver starts from affected services and follows only observed service dependencies; missing or over-budget topology becomes `REQUIRES_REVIEW` instead of silently starting the whole system.

Ports, database names, schemas, queues, and other scarce resources use `RESOURCE_LEASE` keys. Contending workers CAS the same resource token, so an in-process mutex is not the authority. Exact operation retries return the original resource; a live process identity prevents age-based stealing.

## Data performance

`IDatabasePerformanceAdapter` separates database-specific evidence collection from the engine. The first adapter uses real SQLite metadata, `EXPLAIN QUERY PLAN`, and an explicitly authorized disposable write-concurrency fixture. Read-query analysis rejects non-read SQL. It reports full scans and observed index use without modifying production schema.

N+1 findings require repeated identical query fingerprints with multiple parameter fingerprints inside one parent operation. Migration risk consumes structured changes and row estimates, requiring rehearsal and rollback evidence for table rewrites, destructive or irreversible operations, blocking large-index builds, and large backfills.

Performance evidence is evaluated against an explicit contract and before/after baseline. Functional success cannot hide a P95/P99, QPS, error-rate, pool-wait, lock-wait or deadlock violation. Pool and lock observations produce separate assessments, while ordered concurrency samples identify an observed saturation point without claiming one when the fixture never reached it. Metrics not captured by a representative real fixture remain `NOT_MEASURED` rather than being synthesized.
