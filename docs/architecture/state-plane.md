# Hiker 5.19 Project State Plane

## Authority

The SQLite database at `.ai/state/hiker-state.db` is the only active authority for 5.19 project state. Community 5.18 JSON files may be imported once as immutable source evidence. The Pro runtime never writes those legacy files and never treats a projection, export, chat transcript, or materialized view as a second authority.

## Commit contract

Every domain mutation carries a stable `operation_id`, an expected entity generation, bounded semantic event metadata, and the full dependency-edge delta. A single `BEGIN IMMEDIATE` transaction writes the operation journal, domain row, dependency edges, semantic event, and completed operation result. A stale generation returns `STALE_CONTEXT`; an operation ID bound to different facts returns `OPERATION_ID_CONFLICT`; an exact retry returns `IDEMPOTENT_REPLAY` without another domain write.

Reasoning never runs while a database transaction or writer lease is held. The coordinator first returns an immutable snapshot and binding token, releases the connection, then later performs a short compare-and-swap commit.

## Storage tiers

- SQLite/WAL: authoritative mutable state, generations, operation journal, semantic events, dependency edges, materialized-view metadata, artifact references.
- Content-addressed files: large evidence, logs, snapshots, reports, and other blobs under `.ai/artifacts/sha256/<prefix>/<hash>`.
- Archive: verified SQLite backups and cold state artifacts.
- Projection/export/cache: rebuildable and never authoritative.

## Migration and recovery

`hiker migrate-community` reads only the documented 5.18 allowlist plus the top level of `.ai/tasks/*.json` and `.ai/runtime/checkpoints/*.json`. It deliberately excludes prompts, chat history, active-context Markdown, and execution logs. Each imported file has its own idempotent operation; the final migration marker is written only after all files are valid. Re-running a complete import performs no writes.

Every import creates a verified pre-import SQLite backup. `hiker restore` requires the operator to provide the backup's SHA-256 and automatically takes a safety backup of current state. If the restored database fails Doctor, the safety backup is restored.

## Doctor and GC

Doctor checks SQLite integrity, schema version and checksum, legal lifecycle/retention values, incomplete operations, and artifact path existence without resolving outside `.ai`. Repair is intentionally conservative: it may rebuild indexes and optimize the database, but it does not fabricate missing state or delete user evidence.

GC archives stale and superseded durable facts. It may delete only explicitly `CACHE` or `TEMP` entities by age. Durable user facts and referenced artifacts are never silently removed.
