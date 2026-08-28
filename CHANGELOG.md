# Changelog

## 5.19.0-rc.8 — Unreleased

### Added

- `GA-5.19-CLI-BRIDGE-CONTRACT-001`：所有 `--json` 机器命令统一使用 `hiker-cli/v1` 单文档 Envelope，stdout 只承载主响应，stderr 只承载诊断，退出码与 Envelope 一致，并显式报告 Pro 接管状态。
- `GA-5.19-LEGACY-AUTO-CONVERGENCE-001`：普通 `attach` 在安全边界按 Project Preflight 自动执行已知 Legacy migration、旧运行态 Finalize、Doctor 复检和 Adoption；migrate/finalize operation identity 由 Project、来源指纹、迁移 generation 与操作类型确定性生成。
- 新增稳定 Launcher、不可变 Runtime 目录、原子活动 Pointer、版本/协议/schema 兼容门禁、回滚和 operation runtime identity；新增有界 `project-facts` 路由事实接口。

### Safety

- 已有 Project Identity 始终复用；半迁移的同源 Community Fact 按当前 generation 收敛，其他活动 authority 占用同一事实键时失败关闭。
- 非权威脚本、文档和二进制原地惰性保留且不执行；疑似 State Authority、未知 Legacy schema、reparse/path escape、损坏和超出盘点预算的数据返回结构化阻断，不会被误标为安全内容。

### Verification

- 私有源码完整 Debug / Release Unit / Integration：201/201 PASS；Installed Field 与商业 Gate 仍须绑定 rc.8 提交重新执行。

## 5.19.0-rc.7 — Unreleased

### Added

- `GA-5.19-LIVE-ADOPTION-001`：新增 `attach`、`checkpoint` 与自动 `resume` 接管，在自然安全边界把 Community 5.18 的当前 Goal、Task、Checkpoint 和既有 Codex Provider Session 绑定到同一 5.19 Project。
- Community 侧仅提供 Pro 检测、安全边界触发与 fallback；Project Bootstrap、State Plane、Ownership、Context Resolver、Recovery 与幂等事务继续只由 Pro Runtime 实现。

### Safety

- Provider Session 原始标识不写入 State/Event/Evidence/CLI JSON，仅保存 SHA-256 指纹；接管不恢复 Legacy Lease、Agent、Process 或创建第二个 Task/Worktree。
- 活动上下文依赖边按 Turn 原子替换，100 轮接管与 Checkpoint 后热图和 Context 读取保持有界；冷历史不会进入默认 Context。

### Verification

- 私有源码完整 Unit / Integration：126/126 PASS；Community 核心插件：76/76 PASS，其中薄桥 6/6 PASS。真实原长期 Codex 会话 Field 接管仍是 rc.7 宣布 Live Adoption PASS 前的强制门禁。

## 5.19.0-rc.6 — 2026-08-27

### Added

- `GA-5.19-LEGACY-INVENTORY-002`：新增 Preserved Untrusted Legacy Content 分类，对非权威旧脚本、文档、二进制和普通工具JSON执行有界只读盘点、原地保留与Inventory fingerprint，不执行、不导入、不删除、不移动。
- 仅含安全保留内容而无可导入Fact的Legacy项目仍创建稳定Project ID和零Fact Migration Marker，避免被误判为全新项目。

### Safety

- Hiker权威文件名、权威JSON包络、未知受支持Schema、reparse point、path escape与超过10,000项的目录保持 `UNKNOWN_REQUIRES_REVIEW`；Importer仍只读取固定Community Source Catalog。
- 可执行扩展名不代表受信任；脚本与可执行文件只作为惰性字节保留，迁移路径没有执行入口。

### Verification

- 私有源码完整 Unit / Integration：110/110 PASS；新增Python零执行/零修改/零删除/零移动、普通JSON保留、Ownership/Identity伪装阻断、Junction逃逸、path traversal、无界目录和零Fact迁移健康闭环测试。

## 5.19.0-rc.5 — 2026-08-27

### Added

- `GA-5.19-LEGACY-INVENTORY-001`：新增 Legacy `.ai` 语义 Inventory，对 `architecture`、`requirements`、`evidence`、`knowledge` 与 `logs` 区分已知内容、安全保留内容、可迁移事实和未知待审内容。
- 仅将符合 Community 5.18 真实结构的 Architecture、Requirement、Evidence 与 Knowledge metadata 导入 State Plane；Opaque Evidence、Knowledge DB 与 Historical Logs 保留原文件但不进入 Active State。

### Safety

- 不再因存在可安全保留的旧目录而阻断合法 Community 迁移；未知结构、格式不匹配、重解析点与超出盘点预算的内容仍为 `UNKNOWN_REQUIRES_REVIEW`。
- 迁移前深度验证 Legacy Inventory；完成接管后的日常 `status/doctor` 只读顶层与热 State，不随冷 Evidence/Logs 历史线性扫描。

### Verification

- 私有源码完整 Unit / Integration：104/104 PASS；包含脱敏旧布局 CLI 迁移、自动 Project ID、原文件哈希保持、非活动事实归档、任意 JSON 不导入、未知/畸形内容零状态写入、Finalize、Status、Doctor 与双重幂等回归。

## 5.19.0-rc.4 — 2026-08-27

### Added

- `GA-5.19-LEGACY-FINALIZE-001`：新增 `hiker finalize-legacy`，对已完成 Community 迁移的项目自动解析原 Project ID、创建 State Backup 并单事务历史化 Legacy Runtime 控制态。
- 已知 `COMMUNITY_5_18_IMPORT / 2.0.0` Task、TaskDependency、Checkpoint、Ownership、Worktree、RuntimeLease、ResourceLease 与 Agent 统一转为 `Archived`；原 payload、authority、schema、source/dependency fingerprint 保持不变。
- Finalize 完成后立即运行 Doctor；首次返回 `FINALIZED`，重试返回 `ALREADY_FINALIZED` 且不增加 generation、event 或 backup。

### Safety

- Finalize 不生成 TaskId、WriterRuntimeId、WorktreeId、Lease、Agent 或 Process，不创建任何新的 Active Runtime 状态。
- 未完成 migration marker、未知 schema 和 Corrupt Legacy Runtime 事实保持 `REQUIRES_REVIEW`，不会被静默归档。

### Verification

- 当前私有源码完整 Unit / Integration：101/101 PASS；新增阻断前 Doctor、单事务 Finalize、原始事实保留、非运行态 Evidence 保留、恢复备份、未知 schema 拒绝、CLI 健康闭环及重试不增 generation 测试。

## 5.19.0-rc.3 — 2026-08-27

### Added

- `GA-5.19-IDENTITY-001`：统一 Project Bootstrap/Preflight，覆盖新项目、Community 5.18 Legacy、活动项目、移动、显式 Fork、Identity 缺失/冲突/不匹配、损坏状态和未完成迁移。
- `PROJECT/hiker-project-identity` 成为稳定身份事实；普通读命令不创建 UUID，Runtime Start 不能绕过 Preflight，显式 Fork 原子继承可复用事实并重置运行绑定。
- `GA-5.19-RUNTIME-001`：Windows x64 Release 改为目录式 self-contained .NET 9 Runtime，新增无全局 .NET 环境执行与包内 Runtime 完整性 Gate。

### Compatibility

- 现有 Community 5.18 导入数据库在缺少正式 Identity 实体时，从唯一 state namespace 与完整 migration marker 只读恢复原 Project ID；不会生成第二个 UUID或重复导入。
- Legacy 源文件、冷归档和现有 `.ai` 内容不被默认删除、清空或重建。

### Verification

- 当前私有源码完整 Unit / Integration：98/98 PASS；包含新建/重复初始化、Legacy namespace 恢复、Identity 缺失/冲突/不匹配、移动、显式 Fork、迁移中断、损坏数据库和无机器级 .NET 的真实 `win-x64` publish 执行。

## 5.19.0-rc.2 — 2026-08-27

### Fixed

- `FIELD-5.19-001`：Community 5.18 aggregate Ownership / Task Map 不再被解释为活动 5.19 Runtime Ownership / Worktree。
- Project-scoped `doctor/status` 对 Legacy 或缺失必填标识符的状态返回结构化、可诊断的 Health Finding，不再抛出 `ArgumentNullException(key)`。
- rc.1 已导入且生命周期仍为 `Valid` 的 Legacy 事实可被只读识别；新导入的同类事实标记为 `Incompatible`，payload、source fingerprint、migration marker 与幂等语义保持不变。
- Project Resume 不再把 Legacy Task Map 暴露为活动 Worktree。

### Verification

- Pro Runtime：82/82 PASS，其中新增 13 项 Field Fixture、CLI 链路、混合状态、幂等迁移与 Backup/Restore 回归。
- Community 5.18：五插件 297/297 PASS，公开源码工作区无残留差异。
- 真实项目只读复检在 rc.2 完整 Release Gate 后执行，本文不提前声明现场通过。

## 5.19.0-rc.1 — 2026-08-26

### Added

- SQLite/WAL Project State Plane、短事务 CAS、operation 幂等、Doctor、GC、Backup、Restore 与显式 State Export。
- provider-neutral Runtime Contract、Codex CLI Adapter、Worktree 隔离、Handoff、Checkpoint、Crash Recovery 与资源释放。
- 风险自适应冲突调度、独立 Assurance Lane 与串行授权的持久化 Merge Queue。
- 有界 Context Resolver、Project Resume、Intent/Dependency/Evidence Graph、Service Topology、Resource Lease 与 Data Performance Engine。
- Runtime/Script/Middleware 工程资产、最小运行集、性能契约、Pool/Lock 分析与饱和点判定。
- 原子 Goal Change：六类修订、四种 Task 影响分类、作用域证据失效、未受影响成果保留与幂等回执。
- 结构化 Decision Memory：保留理由、来源、作用域、证据、拒绝备选，并原子记录 `DECISION_SUPERSEDED`。
- Doctor/GC 深化：检测状态代际、断边、损坏Projection、Artifact完整性和孤儿内容，并只自动修复可重建或超期对象。
- PostgreSQL Fixture readiness 以目标数据库内真实 `SELECT 1` 为准，避免 `pg_isready` 对不存在数据库的假阳性。
- RC publish 使用确定性 `PathMap`，禁止本机仓库绝对路径进入发布程序集。
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
