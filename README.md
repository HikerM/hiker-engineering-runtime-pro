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

## 已实现的首个垂直切片

当前 State Plane 切片已具备真实 SQLite/WAL、短事务 CAS、operation 幂等、依赖边原子提交、语义事件冲突检测、内容寻址 Artifact、Doctor、保守 GC、可验证备份/恢复，以及 Community 5.18 `.ai` 状态的白名单单向导入。它仍是开发基线，不代表完整 5.19 Runtime 或商业发布就绪。

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

私钥、签名密钥和真实商业License禁止进入Git、Fixture、日志或发行包。
