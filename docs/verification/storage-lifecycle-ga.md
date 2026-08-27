# GA-5.19-STORAGE-LIFECYCLE-001

## CURRENT_STORAGE_BASELINE

保留唯一 SQLite State Plane、`StateCoordinator` Writer、indexed entities/dependency edges、materialized views、content-addressed artifacts、SHA-256 去重与既有 RetentionTier。没有创建第二套 JSON/Markdown Memory 或第二个状态 Writer。新增 `storage-lifecycle-v1` 为同一 SQLite 的 additive schema extension；旧 5.19 数据库首次打开时幂等补齐，后续进程只做有界 schema receipt 检查。

## HOT_WARM_COLD_ARCHIVE

HOT/WARM 继续承载当前 Goal、Task、Decision、Checkpoint、Evidence index 与 Runtime facts。STALE/SUPERSEDED/ORPHANED/ARCHIVED 实体和历史边通过显式 maintenance 事务物理移入 `archived_entities` / `archived_dependency_edges`，不再参与日常查询。CACHE/TEMP 只进入预算与可重建策略，不在普通 Turn 自动清理。

## STATE_SNAPSHOT

`RuntimeStateSnapshot` 绑定 project、generation、event sequence、schema、fingerprint、当前 HOT/WARM entities 和 CURRENT graph。恢复使用 Snapshot + snapshot 后的 `state_deltas`，不从 Day 1 重放。快照 immutable、generation-bound、schema-bound，payload 在写入前执行 secret policy。

## EVENT_ROLLUP

旧 `engineering_events` 以 content-addressed JSON Segment 归档，状态机为 `MARKED_FOR_ARCHIVE → ARCHIVE_VERIFIED → COMPLETE`。只有 Segment 文件 SHA-256 复验通过后，才在同一 SQLite 事务写 pointer/dedup 并删除对应 hot events。`state_deltas` 只删除到 `segment.to_generation`，未归档的新事件 delta 保留。

## OPERATION_RETENTION

保留 active/incomplete、最近完成、Release/Checkpoint 与 Release-bound operation；过期 completed operation 物理移入 `archived_operations`。历史 receipt 仍参与 operation-id 幂等检查，归档不允许重放业务动作。

## DEPENDENCY_RETENTION

日常 Impact/Context/Scheduler 查询只读取 VALID graph。STALE/SUPERSEDED/HISTORICAL 或端点已失效的关系显式移入 `archived_dependency_edges`，历史图只通过专用历史入口读取。

## ARTIFACT_REACHABILITY

`StorageReachabilityAnalyzer` 从当前 Task、有效 State、Release、Evidence、Checkpoint 与 audit/recovery roots 分类 `LIVE / RETAINED / COLD / ARCHIVABLE / UNREACHABLE`。Release、Evidence 与 Checkpoint 引用优先保留，即使对应事实已历史化。

## ARTIFACT_GC

GC 使用 `MARKED_FOR_SWEEP → SOURCE_ELIGIBLE_FOR_DELETE → COMPLETE` receipt。只有 `UNREACHABLE + retention expired + 无 audit/recovery root` 才删除；先在事务中删除 metadata/link/verification，再删除已证明无引用的物理对象。相同 SHA 继续只保留一个对象。

## SQLITE_COMPACTION

Compaction 只在显式 maintenance、freelist ratio/DB size 达标且无 active Ownership/Lease/Worktree/incomplete operation/recovery 时运行。执行前创建并验证 State Backup，WAL checkpoint 后 `VACUUM`，再 checkpoint；活动 Writer 场景确定性返回 `BLOCKED_ACTIVE_WRITER_OR_RECOVERY`。

## STORAGE_BUDGET

Budget 分别覆盖 hot state、warm state、cache、temp、artifact、archive。它只输出超限项与 `TEMP → CACHE → unreachable artifact → cold transition → archive/compact` 建议，不直接删除数据，也不会优先删除有效 Evidence、Decision、Checkpoint 或 release proof。

## FAST_DOCTOR

`hiker doctor` 默认只检查 SQLite/Schema/Generation、当前 hot facts、有效 graph、当前/近期 artifact receipt 与近期 archive pointer。稳定数据库复用 `.ai/cache/storage-fast-doctor-receipt.json`，不重 Hash 未变化 artifact，不扫描冷 Archive。

## DEEP_DOCTOR

`hiker doctor --deep` 才完整 Hash 注册 artifact、校验全部 Event Segment、枚举 content-addressed orphan 和历史依赖。普通 `status/resume/attach/checkpoint/context` 不隐式进入 Deep Doctor。

## INCREMENTAL_VERIFICATION

immutable artifact 保存 `sha256 / size / last_write_utc / verified_at / source_generation / status`。Fast Doctor 复用未变化 receipt；文件身份变化、metadata mismatch、corruption signal 或显式 Deep Doctor 才重 Hash。

## RETRIEVAL_INTEGRITY

Context root 强制验证 Goal/Task authority、VALID lifecycle 与相同 Goal Revision；候选按 Authority → Lifecycle → GoalRevision → dependency relevance 排序。STALE、SUPERSEDED、revision mismatch、Legacy、Cache 与 Runtime Observation 不得冒充当前事实。旧文本更相似或更新时间更新不能覆盖 Authority/Revision。

## AUTHORITY_RESOLUTION

统一分类 `AUTHORITATIVE_CURRENT / VERIFIED_DERIVED / RUNTIME_OBSERVATION / HISTORICAL / LEGACY_PRESERVED / CACHE`。默认 Context 只接收前两类。Community 5.18 源在 Live Adoption 后继续保留原 `source_authority / source_fingerprint / source_ref`，避免每 Turn 把运行时包装指纹误当工程来源。

## RETRIEVAL_RECEIPT

每次 Context 构造返回 project/goal/task、store generation、selected refs/bytes、state reads、graph queries、archive/cold reads、stale/superseded/authority rejection、context fingerprint、receipt fingerprint 与 latency。Receipt 不保存 Prompt、聊天或源码正文。

## LEGACY_INCREMENTAL_MIGRATION

Legacy top-level segments 保存 bounded metadata fingerprint、files、bytes、status 与 verified time。未变化 Segment 复用已验证导入结果，变化 Segment 重新处理；reparse point 不跟随，100,000 files/100GB 单段预算超限时结构化阻断。该盘点只在 migration/显式 Field maintenance 使用，不进入普通 hot path。

## BACKUP_STRATEGY

State Backup 保留完整 SQLite 安全备份。Artifact Backup 独立使用 SHA object reuse，只复制 reachable 且目标中不存在的对象，并生成 durable manifest；相同 SHA 不重复复制。不会在每次 Checkpoint 复制整个 `.ai`。

## CRASH_SAFE_MAINTENANCE

Rollup、Snapshot、Operation/Dependency/Entity retention、Artifact GC、Compaction 均使用稳定 operation-id 与 receipt。故障注入证明：Archive pointer 事务失败后 receipt 停留 `ARCHIVE_VERIFIED`，重试复用已验证 Segment，不重复 event；损坏 Segment 明确失败，不删除 hot source。

## CONTEXT_COST

20,000 个物理历史事实归档前后：Context 均为 509 bytes、5 次 state read、0 archive read。真实 4.249GB Field 副本：Context 6,062 bytes、6 次 state read、0 archive read；默认上下文大小与当前 Task 相关，不与历史物理容量线性增长。

## HOT_PATH_PERFORMANCE

本机进程内确定性测量，不外推为跨机器 SLA：

- 合成历史：Context P50/P95 从 `0.1440/0.1685 ms` 到 `0.1160/0.1576 ms`；Fast Doctor P95 从 `1.8962 ms` 到 `0.9165 ms`。
- 真实 Field 副本：status P50/P95/P99 `4.924/8.236/8.236 ms`；doctor `1.624/3.262/3.262 ms`；attach/resume 只读重试 `7.096/9.453/9.453 ms`；checkpoint 幂等重试 `1.691/2.602/2.602 ms`；evidence lookup `0.019/0.020/0.024 ms`；current graph probe `0.048/0.136/0.190 ms`；current Goal lookup `0.020/0.067/0.072 ms`。
- Fast Doctor：0 artifact scan、0 archive scan；Context：0 cold/archive read。

## LARGE_AI_FIELD

使用真实 `>=1GB` 项目的隔离读写副本，不在真实项目上做性能实验：

- 物理快照：4,249,084,733 bytes，25,420 files。
- verified preserved segments：4,245,509,888 bytes，25,395 files；首次 metadata inventory `460.839 ms`，12 个未变化 Segment 复用。
- `ai_total_bytes` 有界索引值：4,246,669,056 bytes；State DB：1,159,168 bytes。
- Project ID、Current Goal、Current Task 均不变；Decision `3 → 3`；Evidence `9 → 10`（新增 Field checkpoint evidence，未删除旧 Evidence）。
- Resume/Attach/Context/Checkpoint 全链成功；重复 attach 为 `ALREADY_ADOPTED` 且 0 state write。
- 原项目复核：25,419 files、4,248,965,731 bytes、State DB SHA-256 `f0c2f05b58dae00c5629b6fc67f294befa60b7c0ac506bdae1b05d90f4d8a731`，与复制前一致，零修改。

## TESTS

- Storage 定向：14/14 PASS。
- Live Adoption/CLI 定向：17/17 PASS。
- 全量 Debug：188/188 PASS。
- 全量 Release：188/188 PASS。
- Synthetic Storage Scenario：3/3 PASS。
- Real Project Storage Field：1/1 PASS。
- Commercial Security COMM-G4–G15：PASS。
- `dotnet format --verify-no-changes`：PASS。

## FILE_SIZE

Storage 生产文件按职责拆分；最大 Storage 文件为 `SqliteStateStore.StorageDoctor.cs` 352 行，Field verifier 236 行，Storage tests 最大 299 行。没有新增单文件总控、第二套 State Plane 或第二个 Writer。

## FINAL_GATES

- `PHYSICAL_HISTORY_DECOUPLED_FROM_HOT_PATH=PASS`
- `CONTEXT_SIZE_BOUNDED=PASS`
- `HOT_PATH_NON_LINEAR_TO_PROJECT_AGE=PASS`
- `FAST_DOCTOR=PASS`
- `ARTIFACT_REACHABILITY=PASS`
- `EVENT_ROLLUP=PASS`
- `SNAPSHOT_RECOVERY=PASS`
- `SQLITE_COMPACTION=PASS`
- `RETRIEVAL_AUTHORITY=PASS`
- `RETRIEVAL_ACCURACY=PASS`
- `LEGACY_LARGE_PROJECT=PASS`
- `CRASH_SAFE_STORAGE_MAINTENANCE=PASS`
- `REAL_FIELD=PASS`

`DATA_LOSS=0`

`AUTHORITY_ERRORS=0`

`STALE_FACT_AS_CURRENT=0`

## VERDICT

`PASS_STORAGE_LIFECYCLE`

本结论只覆盖独立 GA-5.19-STORAGE-LIFECYCLE-001；不改变 Change Intelligence 仍需真实 A/B 成效证据的独立 `BLOCKED_CHANGE_INTELLIGENCE` 状态，也不等于 5.19 GA 总体验收通过。
