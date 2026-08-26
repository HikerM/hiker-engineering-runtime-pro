# Changelog

## 5.19.0-rc.1 — 2026-08-26

### Added

- SQLite/WAL Project State Plane、短事务 CAS、operation 幂等、Doctor、GC、Backup、Restore 与显式 State Export。
- provider-neutral Runtime Contract、Codex CLI Adapter、Worktree 隔离、Handoff、Checkpoint、Crash Recovery 与资源释放。
- 风险自适应冲突调度、独立 Assurance Lane 与串行授权的持久化 Merge Queue。
- 有界 Context Resolver、Project Resume、Intent/Dependency/Evidence Graph、Service Topology、Resource Lease 与 Data Performance Engine。
- Runtime/Script/Middleware 工程资产、最小运行集、性能契约、Pool/Lock 分析与饱和点判定。
- 原子 Goal Change：六类修订、四种 Task 影响分类、作用域证据失效、未受影响成果保留与幂等回执。
- 结构化 Decision Memory：保留理由、来源、作用域、证据、拒绝备选，并原子记录 `DECISION_SUPERSEDED`。
- ECDSA P-256 Signed Entitlement、Device Activation、Offline Grace、Signed Module / Update 与签名回滚策略。
- 加速老化、大型语义仓库、真实进程/端口/HTTP、PostgreSQL、Codex Runtime 和 COMM-G1–G17 验证场景。
- 1/2/4/8 并发 Fake Runtime Worker 场景，覆盖生产 Task/Agent/Lease/Ownership/Worktree 协调与释放。
- 商业 RC 工具：外部私钥签名、确定性 Manifest、逐文件 Hash、ZIP Checksum、独立信任根验证、隐私扫描、篡改拒绝和隔离 Clean Install。

### Security

- Pro 源码仅保存在 Private Repository。
- 发行私钥必须位于仓库与发布输出之外，禁止进入 Git、Client、Fixture、日志或 ZIP。
- License 失效不会阻断 `.ai` 读取、备份、导出和恢复。

### Evidence limits

- 加速 12 个月与 100k 文件属于 `SIMULATION_PROVEN`，不是客户现场证明。
- SQLite、Git/Worktree、Filesystem、Process/Port/HTTP、PostgreSQL、ECDSA 与 Codex CLI 具有代表性真实集成证据。
- `FIELD_PROVEN = NOT_YET_PROVEN`。
