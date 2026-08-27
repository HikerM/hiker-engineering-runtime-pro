# Project Bootstrap and Identity

## Authority

Project Identity 的正式事实是 SQLite State Plane 中唯一有效的 `PROJECT/hiker-project-identity` 实体。它由 `StateCoordinator` 写入，包含稳定 `project_id`、来源、可选父项目、创建时间和仅用于识别移动的路径指纹。路径、分支和 Worktree 都不是 Project ID 来源。

Community 5.18 已完成导入、但尚无正式 Identity 实体的数据库，可以从唯一 state namespace 与完整 migration marker 只读恢复原 ID。读命令不会把该兼容事实偷偷升级成新的 Identity；显式 `init` 才会用同一 ID 完成权威绑定。

## Unified Preflight

`init`、`migrate-community`、`finalize-legacy`、`doctor`、`status`、`resume`、`backup`、`export` 与 Runtime Start 使用同一 Project Preflight。状态模型覆盖：

- `NEW_PROJECT`、`LEGACY_PROJECT`、`ACTIVE_EXISTING_PROJECT`、`MIXED_LEGACY_PROJECT`；
- `IDENTITY_MISSING`、`IDENTITY_CONFLICT`、`IDENTITY_MISMATCH`；
- `PROJECT_MOVED`、`PROJECT_FORKED`；
- `STATE_DAMAGED`、`MIGRATION_INCOMPLETE`、`LEGACY_ALREADY_IMPORTED`、`REQUIRES_REVIEW`。

只读命令在新项目、身份缺失、冲突、损坏或未完成迁移时返回结构化阻断，不创建 `.ai`、数据库或 UUID。写意图只在 `init`、受控 Legacy migration、显式 Fork 或恢复命令中成立。

## Legacy lifecycle

Legacy source 只从固定白名单导入。每个事实保存来源哈希，完成后写 migration marker；原文件保持原位且只作为只读来源。中断后，没有 marker 的已导入事实被识别为 `MIGRATION_INCOMPLETE`，重试复用原 operation ID 和原 Project ID，不重复导入已提交事实。

`.ai` 内容按权威状态、可迁移 Legacy、冷归档、可重建 Cache、临时项和未知项分类。未知顶层内容需要人工复核；任何路径都不默认删除或重建 `.ai`。

迁移完成不等于旧运行现场可继续使用。显式 `hiker finalize-legacy` 在确认唯一 Project ID 与完整 migration marker 后先创建 State Backup，再由 `StateCoordinator` 触发单个 SQLite 事务，将 `COMMUNITY_5_18_IMPORT / 2.0.0` 的 Task、TaskDependency、Checkpoint、Ownership、Worktree、RuntimeLease、ResourceLease 和 Agent 转为 `Archived`。事务只更新 lifecycle、retention、generation 与 updated_at，原 payload、authority、schema 和 fingerprint 不变；Requirement、Decision、Knowledge 和 Evidence 不在写范围内。未知 schema 或 Corrupt 事实继续 `REQUIRES_REVIEW`。

## Move and Fork

移动项目目录保留原 Project ID。根路径指纹变化只产生 `PROJECT_MOVED`，`init` 可确认新位置，但不会改变身份。

Fork 必须由 `hiker fork --project-id <new-id>` 显式执行。一个 SQLite 事务完成：新 Identity、可复用 Requirement/Decision/Architecture/Knowledge/Evidence 复制、旧 namespace 归档、Task/Lease/Ownership/Worktree/Checkpoint/Agent 不继承、依赖边按继承范围复制、Artifact 所有权转移以及 operation/event 记录。操作前建立安全备份；相同目标 ID 重试只读取既有 operation receipt。

## Recovery invariant

- 多个有效 Identity 或多个活动 namespace：`IDENTITY_CONFLICT`，禁止选择“第一个”。
- State DB 存在业务事实但没有可证明 Identity：`IDENTITY_MISSING`，读命令禁止自动修复。
- 数据库损坏或 identity payload 不一致：`STATE_DAMAGED` / `RECOVERY_REQUIRED`。
- Runtime Start 必须先通过同一 Preflight，不能用请求参数绕过 Project Identity。
