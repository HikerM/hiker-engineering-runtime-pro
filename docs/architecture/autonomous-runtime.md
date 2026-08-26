# Autonomous Runtime and Writer Safety

## Provider-neutral contract

`IExecutionRuntimeAdapter` defines `start`, `resume`, `pause`, `interrupt`, `cancel`, `status`, `checkpoint`, output/artifact/event collection, `recover`, and `shutdown`. Core state contains only Hiker runtime IDs, adapter IDs, lifecycle, process identity, checkpoint references, and bounded semantic facts. Codex-specific sessions and event formats are adapter concerns and are not valid State Plane schema.

An adapter must start suspended in `STARTING`. It cannot write task surfaces before the coordinator atomically reserves Task, Agent, Runtime Lease, Ownership, and Worktree. Only after that reservation may the adapter enter `RUNNING`.

## Writer and worktree invariants

- One writer Task has exactly one active Worktree state.
- An active workspace path cannot belong to two tasks.
- Predicted affected scopes are normalized repository-relative paths. Overlapping scopes reduce parallelism by rejecting the new writer before its adapter starts.
- Task, Agent, Runtime Lease, Ownership, and Worktree move at one State Plane generation.
- Lease age is never proof of death. A matching PID, process start time, executable fingerprint, and host fingerprint prevents takeover even after expiry.

## Safe handoff

Handoff pauses the current runtime, validates Git workspace facts, creates a checkpoint, and writes a content-addressed `hiker-minimal-handoff/v1` package. The package contains IDs, goal revision, checkpoint reference, Git HEAD/branch/status fingerprint, bounded relevant-state references, and acceptance IDs. It explicitly excludes complete prompts, chat history, source files, and stream deltas.

The target adapter starts suspended. A single batch transfers Task, Agent, Lease, Ownership, Worktree, and Checkpoint facts. The target then recovers and a second short batch marks it running. If a transfer has committed but activation is uncertain, retry returns `RECOVERY_REQUIRED`; it never starts another target blindly.

## Crash recovery

Recovery requires a durable Checkpoint entity whose ID, source fingerprint, and artifact reference match the supplied checkpoint. It also requires the current Git status fingerprint to match the checkpoint boundary. A live or possibly-live prior writer blocks automatic recovery. Only a failed/interrupted/cancelled owner, or an unreachable owner after lease expiry, can be replaced.

## Completion and release

Completion is two phase. First, bounded output, artifacts, and semantic events are collected and the writer set enters `DRAINING`. After adapter shutdown is observed, Task reaches its terminal state while Agent, Lease, Ownership, and Worktree are archived as released facts. A crash between phases is visible as `RECOVERY_REQUIRED`; duplicate completion is rejected by operation receipts.
