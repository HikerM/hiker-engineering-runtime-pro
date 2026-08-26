# Hiker 5.19 Pro Architecture

Hiker 5.19 Pro 是本地编译的长期自主软件工程运行时。它不替代 Agent，也不把模型锁进固定工作流；模型负责理解、设计、实现和诊断，Runtime 负责持久状态、确定性边界、恢复与商业信任。

正式架构只包含七个一级域：

1. Project State Plane：SQLite/WAL、StateStore、短事务 CAS、Generation、Doctor、GC、Backup、Export 与 Content-Addressed Artifact。
2. Autonomous Agent Runtime：provider-neutral Adapter、Task/Lease/Ownership/Worktree 协调、Checkpoint、Handoff、Recovery 与两阶段 Release。
3. Durable Engineering Memory & Context Economics：HOT/WARM/COLD 分层、有界 Context Resolver 与 Project Resume。
4. Data Performance & Concurrency Engineering：真实 Query Plan、Index、并发、N+1 与 Migration Risk Evidence。
5. Polyglot Service & Runtime Engineering：Runtime Profile、Service Topology、Contract 与 Resource Lease。
6. Intent / Dependency / Evidence Graph：结构化依赖、Affected Scope、Goal Change 与 Evidence Invalidation。
7. Commercial Runtime & Trust Architecture：签名 Entitlement、Device Activation、Offline Grace、Module/Update Integrity 与默认拒绝降级。

权威设计文档：

- [5.19 Architecture](docs/architecture/5.19-architecture.md)
- [Project State Plane](docs/architecture/state-plane.md)
- [Autonomous Runtime](docs/architecture/autonomous-runtime.md)
- [Context, Graph, Services and Data](docs/architecture/context-graph-data.md)
- [Commercial Trust](docs/architecture/commercial-trust.md)
- [Community / Pro Boundary](docs/architecture/community-pro-boundary.md)

商业边界：公开的 Community 5.18 不包含 Pro 源码；Pro 私有仓库不保存发行私钥。`.ai` 属于用户，License 失效只能限制 Pro 高级执行，不能删除、锁死或损坏状态、Git、Worktree 或 Evidence。
