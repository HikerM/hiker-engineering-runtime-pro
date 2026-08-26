# Hiker 5.19 — Durable Autonomous Engineering Runtime

Hiker 5.19 Pro 是面向长期、异构、大型软件工程的本地商业运行时。它扩展公开的 Hiker Community 5.18，但商业源码、状态协调器、运行时调度、上下文解析、工程图谱、性能引擎和信任体系只存在于本私有仓库。

当前阶段：`DEVELOPMENT`。在全部商业发布门禁通过前，不宣称 `PASS_5_19_COMMERCIAL`。

## 不变量

- Public Community 5.18 保持免费且不包含 Pro 实现。
- 模型负责需求理解、架构推理、实现与诊断；Runtime 负责状态、CAS、租约、验证和确定性边界。
- Agent 推理期间不持有数据库事务或写锁。
- Cache 与 Materialized View 永远不是权威事实。
- License 失效不得损坏、锁死或删除用户 `.ai`、Git、Worktree、Evidence。
- 模拟、真实集成和现场验证必须分别报告。

## 当前基线

- Community repository: `HikerM/hiker-workflow-skill-pack`
- Community baseline: `main@05e94e32af719c81f740fe2af08c35f98a527588`
- Community version: `5.18.0+codex.20260826161321`
- Pro repository: `HikerM/hiker-engineering-runtime-pro` (`PRIVATE`)
- Pro target version: `5.19.0`
- Runtime implementation: .NET 9 compiled local CLI/runtime
- State schema target: `3.0.0`

设计与证据入口：

- [5.18 Real Baseline](docs/baseline/5.18-real-baseline.md)
- [5.19 Gap Analysis](docs/architecture/5.19-gap-analysis.md)
- [Community / Pro Boundary](docs/architecture/community-pro-boundary.md)
- [5.19 Architecture](docs/architecture/5.19-architecture.md)
- [Project State Plane](docs/architecture/state-plane.md)
- [Autonomous Runtime and Writer Safety](docs/architecture/autonomous-runtime.md)
- [Context, Graph, Services, and Data Performance](docs/architecture/context-graph-data.md)
- [Commercial Trust Architecture](docs/architecture/commercial-trust.md)

## 当前实现状态

State Plane 已具备真实 SQLite/WAL、短事务 CAS、operation 幂等、依赖边原子提交、语义事件冲突检测、内容寻址 Artifact、Doctor、保守 GC、可验证备份/恢复，以及 Community 5.18 `.ai` 状态的白名单单向导入。

Runtime 已建立 provider-neutral Adapter contract、Task/Agent/Lease/Ownership/Worktree 同代提交、PID+启动时间+可执行身份验证、最小交接包、崩溃恢复、两阶段资源释放、真实 Git Worktree 管理和 Codex CLI Adapter。真实安装的 Codex 已完成 Task、隔离 Worktree、Checkpoint、同 Session Recover 与资源释放 E2E，归类为 `REAL_INTEGRATION_PROVEN`。

Context Resolver、`hiker resume`、Intent/Dependency/Evidence Graph、50 服务语义拓扑、资源租约、SQLite 查询计划与并发证据、签名 Entitlement、设备激活、Offline Grace、签名 Module/Update、默认拒绝降级和签名回滚授权均已实现。

验证工具已覆盖：

- 12 个月加速老化：1201 Task、120 Goal Revision、120 Handoff、60 Crash；
- 1/2/4/8 Worker 的真实 SQLite/WAL CAS 竞争；
- 100k 非空语义文件与真实 Git add/commit/delta；
- 5 个隔离可执行 HTTP 服务；
- PostgreSQL 16.14、100k 行 before/after 与 8 客户端并发；
- COMM-G1/G2/G3/G16/G17 公私边界和热路径成本；
- 状态、Handoff、Artifact、Migration、Release 的故障注入。

这些结果分别属于 `DETERMINISTIC_VERIFIED`、`SIMULATION_PROVEN` 或 `REAL_INTEGRATION_PROVEN`；不等于客户现场长期运行。当前仍是开发基线，不代表完整 5.19 商业发布就绪。

```powershell
dotnet run --project src/Hiker.Pro.Cli -- init --root <project>
dotnet run --project src/Hiker.Pro.Cli -- doctor --root <project>
dotnet run --project src/Hiker.Pro.Cli -- backup --root <project>
dotnet run --project src/Hiker.Pro.Cli -- migrate-community --root <project> --project-id <stable-id>
```

## 构建

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

## 可重复验证

验证输出写入被 Git 忽略的 `artifacts/verification/`：

```powershell
dotnet run --project tools/Hiker.Pro.Verification -c Release -- --scenario state-aging --root .
dotnet run --project tools/Hiker.Pro.Verification -c Release -- --scenario large-repository --root . --file-scale 100000
dotnet run --project tools/Hiker.Pro.Verification -c Release -- --scenario real-services --root . --docker
dotnet run --project tools/Hiker.Pro.Verification -c Release -- --scenario commercial --root . --public-root <community-5.18-root>
```

完整证据边界和当前实测结果见 [5.19 Verification](docs/verification/5.19-verification.md)。

私钥、签名密钥和真实商业License禁止进入Git、Fixture、日志或发行包。
