# Hiker 5.19 Pro — Durable Autonomous Engineering Runtime

Hiker 5.19 Pro 是面向长期、异构、大型软件工程的本地商业运行时。它扩展公开的 Hiker Community 5.18，但商业源码、状态协调器、运行时调度、上下文解析、工程图谱、性能引擎和信任体系只存在于本私有仓库。

当前阶段：`RELEASE_CANDIDATE`。当前代码是 5.19 商业候选，不是最终正式发布；是否达到 `PASS_5_19_COMMERCIAL` 必须由同一提交上的完整商业 Gate 证明。

## 不变量

- Public Community 5.18 保持免费且不包含 Pro 实现。
- 模型负责需求理解、架构推理、实现与诊断；Runtime 负责状态、CAS、租约、验证和确定性边界。
- Agent 推理期间不持有数据库事务或写锁。
- Cache 与 Materialized View 永远不是权威事实。
- License 失效不得损坏、锁死或删除用户 `.ai`、Git、Worktree、Evidence。
- 模拟、真实集成和现场验证必须分别报告。

## 当前基线

| 项目 | 当前值 |
| --- | --- |
| Pro 产品版本 | `5.19.0-rc.1` |
| Runtime 版本 | `0.1.0` |
| State Schema | `3.0.0` |
| Runtime | .NET 9，本地编译 CLI / Runtime |
| Community 基线 | `HikerM/hiker-workflow-skill-pack` `main@05e94e32af719c81f740fe2af08c35f98a527588` |
| Community 版本 | `5.18.0+codex.20260826161321` |
| Pro 仓库 | `HikerM/hiker-engineering-runtime-pro`，Private |

设计与证据入口：

- [5.18 Real Baseline](docs/baseline/5.18-real-baseline.md)
- [5.19 Gap Analysis](docs/architecture/5.19-gap-analysis.md)
- [Community / Pro Boundary](docs/architecture/community-pro-boundary.md)
- [5.19 Architecture](docs/architecture/5.19-architecture.md)
- [Project State Plane](docs/architecture/state-plane.md)
- [Autonomous Runtime and Writer Safety](docs/architecture/autonomous-runtime.md)
- [Context, Graph, Services, and Data Performance](docs/architecture/context-graph-data.md)
- [Commercial Trust Architecture](docs/architecture/commercial-trust.md)
- [5.19 Release Process](docs/release/5.19-release-process.md)

## 当前实现状态

State Plane 已具备真实 SQLite/WAL、短事务 CAS、operation 幂等、依赖边原子提交、语义事件冲突检测、内容寻址 Artifact、Doctor、保守 GC、可验证备份/恢复，以及 Community 5.18 `.ai` 状态的白名单单向导入。所有生产状态变化收敛到 `StateCoordinator`，测试会阻断新的平行写入口。

Runtime 已建立 provider-neutral Adapter contract、Task/Agent/Lease/Ownership/Worktree 同代提交、PID+启动时间+可执行身份验证、最小交接包、崩溃恢复、两阶段资源释放、真实 Git Worktree、风险自适应冲突调度、持久化 Merge Queue 和 Codex CLI Adapter。低/中风险最多三个互不重叠 Writer Lane；高/关键风险收敛到一个 Writer，并要求独立 Assurance Lane。Merge Queue 只授权已冻结、已验证且仍基于目标 HEAD 的候选，不静默执行合并。

Context Resolver、`hiker resume`、结构化 Decision Memory、Intent/Dependency/Evidence Graph、结构化 Goal Change、50 服务语义拓扑、资源租约、SQLite 查询计划与并发证据、签名 Entitlement、设备激活、Offline Grace、签名 Module/Update、默认拒绝降级和签名回滚授权均已实现。Decision Memory 保留已拒绝方案及原因，并原子替代当前决策；Goal Change 接受模型给出的 `AFFECTED / UNAFFECTED / SUPERSEDED / REQUIRES_REVIEW` 分类，但只由 Runtime 验证 Task/Evidence 绑定并在一个 CAS 事务内提交 Goal Revision、Task 绑定和必要的证据失效。两者都不使用关键词替代语义判断。

验证工具已覆盖：

- 12 个月加速老化：1201 Task、120 Goal Revision、120 Handoff、60 Crash；
- 1/2/4/8 Worker 的真实 SQLite/WAL CAS 竞争，以及基于生产 Runtime Contract 的并发 Fake Adapter 启动/释放；
- 100k 非空语义文件与真实 Git add/commit/delta；
- 5 个隔离可执行 HTTP 服务；
- PostgreSQL 16.14、100k 行 before/after 与 8 客户端并发；
- COMM-G1/G2/G3/G16/G17 公私边界和热路径成本；
- 状态、Handoff、Artifact、Migration、Release 的故障注入。

这些结果分别属于 `DETERMINISTIC_VERIFIED`、`SIMULATION_PROVEN` 或 `REAL_INTEGRATION_PROVEN`；不等于客户现场长期运行。当前是发布候选，最终状态只由同一提交生成的商业 Gate 决定。

## 当前验证结果

以下为 2026-08-26 的本地可重复证据；原始机器相关 JSON 保存在被 Git 忽略的 `artifacts/verification/`。

| 验证面 | 结果 | 证据等级 |
| --- | --- | --- |
| Unit / Integration | 当前源码 66/66 PASS；最终数量以同提交商业 Gate 的 TRX 为准 | `DETERMINISTIC_VERIFIED` |
| Community 5.18 回归 | 五插件合计 297/297 PASS，公开工作区保持干净 | `DETERMINISTIC_VERIFIED` |
| 加速老化 | 12 个合成月份、1201 Task、120 Goal Revision、120 Handoff、60 Crash | `SIMULATION_PROVEN` |
| 并发写入 | 1/2/4/8 Worker 均恰好一个 CAS 成功；Runtime Worker 结果需由当前提交重跑确认 | `SIMULATION_PROVEN` |
| 大仓库 | 100,000 个非空语义文件；100 文件增量 `git status` 269.969 ms | `SIMULATION_PROVEN` |
| 服务集成 | 五个真实进程、唯一端口与 HTTP Health | `REAL_INTEGRATION_PROVEN` |
| PostgreSQL | 16.14、100,000 行；查询 P95 9.528 ms → 0.114 ms；八客户端零错误 | `REAL_INTEGRATION_PROVEN` |
| 商业边界 | 私有仓库可见性、Public Source / ZIP Pro 泄漏、私钥与本机路径扫描通过 | `DETERMINISTIC_VERIFIED` |
| License | 250 次签名验证 P95 1.649 ms；StateStore 热路径 License 检查为零 | `DETERMINISTIC_VERIFIED` |
| Codex Runtime | 真实 `codex-cli 0.150.0-alpha.8` 完成隔离 Worktree、Checkpoint、同 Session Recover 与释放 | `REAL_INTEGRATION_PROVEN` |

Context 在加速老化第 12 月仍为 548 bytes、五次状态读取、零次冷历史扫描；State Query P95 0.024 ms，Context Build P95 0.136 ms，Resume P95 3.310 ms，Affected Scope P95 0.172 ms。

真实客户项目和真实日历 12 个月仍为 `NOT_YET_FIELD_PROVEN`；多 Provider Agent、PostgreSQL Pool Wait / Lock Wait / Saturation Point，以及宿主 Token 节省百分比仍为 `NOT_MEASURED`。

## CLI 使用

前置条件：.NET 9 SDK。运行时项目状态写入目标工程自身的 `.ai`；以下命令不会安装或替换 Codex 插件。

```powershell
dotnet run --project src/Hiker.Pro.Cli -- version
dotnet run --project src/Hiker.Pro.Cli -- init --root <project>
dotnet run --project src/Hiker.Pro.Cli -- doctor --root <project>
dotnet run --project src/Hiker.Pro.Cli -- status --root <project>
dotnet run --project src/Hiker.Pro.Cli -- resume --root <project> --project-id <stable-id> --goal-id <goal-id>
dotnet run --project src/Hiker.Pro.Cli -- backup --root <project>
dotnet run --project src/Hiker.Pro.Cli -- export --root <project> --project-id <stable-id>
dotnet run --project src/Hiker.Pro.Cli -- migrate-community --root <project> --project-id <stable-id>
```

`restore` 必须显式提供备份文件及其 SHA-256，避免误恢复：

```powershell
dotnet run --project src/Hiker.Pro.Cli -- restore --root <project> --backup <file> --confirm-sha256 <sha256>
```

## 构建

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

## 仓库结构

| 路径 | 职责 |
| --- | --- |
| `src/Hiker.Pro.Runtime/State` | SQLite/WAL 权威状态、CAS、事件、Artifact 元数据、Backup/Restore/Export |
| `src/Hiker.Pro.Runtime` | State、Task、Lease、Worktree、Checkpoint、Recovery、Context、Graph 与商业信任边界 |
| `src/Hiker.Pro.Cli` | 面向工程项目的薄 CLI 入口 |
| `tools/Hiker.Pro.Verification` | 老化、规模、真实服务、Codex Runtime 与 COMM-G1–G17 验证 |
| `tools/Hiker.Pro.Release` | 版本事实、签名 RC 构建、独立包校验与商业发布 Gate |
| `tests/` | 单元、集成、故障注入与回归测试 |
| `docs/` | 架构、Public/Pro 边界、证据分级与发布流程 |

## 可重复验证

验证输出写入被 Git 忽略的 `artifacts/verification/`：

```powershell
dotnet run --project tools/Hiker.Pro.Verification -c Release -- --scenario state-aging --root .
dotnet run --project tools/Hiker.Pro.Verification -c Release -- --scenario large-repository --root . --file-scale 100000
dotnet run --project tools/Hiker.Pro.Verification -c Release -- --scenario real-services --root . --docker
dotnet run --project tools/Hiker.Pro.Verification -c Release -- --scenario commercial --root . --public-root <community-5.18-root>
dotnet run --project tools/Hiker.Pro.Verification -c Release -- --scenario commercial-security --root .
dotnet run --project tools/Hiker.Pro.Verification -c Release -- --scenario codex-runtime --root . --codex-execute
```

`real-services --docker` 会启动隔离的 PostgreSQL 测试容器；`codex-runtime --codex-execute` 会调用当前已安装的 Codex CLI。两者均需显式运行，不属于普通开发默认路径。

完整证据边界和当前实测结果见 [5.19 Verification](docs/verification/5.19-verification.md)。

## 商业 RC Gate

RC 构建要求干净的 `main`。一次性验证密钥可通过标准输入传入，避免私钥落盘：

```powershell
openssl ecparam -name prime256v1 -genkey -noout | dotnet run --project tools/Hiker.Pro.Release -c Release -- build `
  --root . --output artifacts/release --private-key-stdin --key-id <rc-key-id> --runtime win-x64 --channel rc

dotnet run --project tools/Hiker.Pro.Release -c Release -- verify `
  --package <rc-zip> --public-key <public-pem> --public-key-sha256 <pinned-fingerprint> `
  --key-id <rc-key-id> --report artifacts/verification/release-package.json

dotnet run --project tools/Hiker.Pro.Release -c Release -- gate `
  --root . --public-root <community-5.18-root> `
  --package-report artifacts/verification/release-package.json `
  --report artifacts/verification/commercial-release-gate.json
```

`verify` 的 Clean Install 在隔离临时目录中执行，不安装或替换当前 Codex 插件。Gate 的唯一通过结果是 `PASS_5_19_COMMERCIAL`；任一事实缺失或不匹配均返回 `BLOCKED_5_19`。

完整签名构建、独立信任根和聚合 Gate 说明见 [5.19 Release Process](docs/release/5.19-release-process.md)。私钥、签名密钥和真实商业 License 禁止进入 Git、Fixture、日志或发行包。
