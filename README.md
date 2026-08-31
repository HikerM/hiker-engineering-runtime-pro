# Hiker 5.19 Pro — Durable Autonomous Engineering Runtime

Hiker 5.19 Pro 是面向长期、异构、大型软件工程的本地商业运行时。它扩展公开的 Hiker Community 5.18，但商业源码、状态协调器、运行时调度、上下文解析、工程图谱、性能引擎和信任体系只存在于本私有仓库。

当前阶段：`RELEASE_CANDIDATE`。当前代码是 5.19 商业候选，不是最终正式发布；是否达到 `PASS_5_19_COMMERCIAL` 必须由同一提交上的完整商业 Gate 证明。

## 不变量

- Public Community 保持免费且只提供轻量能力与薄 Bridge，不包含 Pro 实现。
- 模型负责需求理解、架构推理、实现与诊断；Runtime 负责状态、CAS、租约、验证和确定性边界。
- Agent 推理期间不持有数据库事务或写锁。
- Cache 与 Materialized View 永远不是权威事实。
- License 失效不得损坏、锁死或删除用户 `.ai`、Git、Worktree、Evidence。
- 模拟、真实集成和现场验证必须分别报告。

## 当前基线

| 项目 | 当前值 |
| --- | --- |
| Pro 产品版本 | `5.19.0-rc.16` |
| Runtime 版本 | `0.1.0` |
| State Schema | `3.0.0` |
| Runtime | .NET 9；`win-x64` 发行包自包含 Runtime，目标机无需预装 .NET |
| Community 基线 | `HikerM/hiker-workflow-skill-pack` 候选 `fix/ga-5.19-field-closure@b29c7ca6c21ff0c3bbfb14fd65195fd799e3a9dc`（Draft，未合并 `main`） |
| Community 版本 | `5.19.0+codex.20260831013125` |
| Pro 仓库 | `HikerM/hiker-engineering-runtime-pro`，Private |

设计与证据入口：

- [5.18 Real Baseline](docs/baseline/5.18-real-baseline.md)
- [5.19 Gap Analysis](docs/architecture/5.19-gap-analysis.md)
- [Community / Pro Boundary](docs/architecture/community-pro-boundary.md)
- [5.19 Architecture](docs/architecture/5.19-architecture.md)
- [Project State Plane](docs/architecture/state-plane.md)
- [Project Bootstrap and Identity](docs/architecture/project-bootstrap-identity.md)
- [Autonomous Runtime and Writer Safety](docs/architecture/autonomous-runtime.md)
- [Stable Runtime Activation](docs/architecture/runtime-activation.md)
- [Environment & Resource Control Plane](docs/architecture/environment-resource-control.md)
- [Change Intelligence](docs/architecture/change-intelligence.md)
- [Context, Graph, Services, and Data Performance](docs/architecture/context-graph-data.md)
- [Commercial Trust Architecture](docs/architecture/commercial-trust.md)
- [5.19 Release Process](docs/release/5.19-release-process.md)
- [Change Intelligence GA Report](docs/verification/change-intelligence-ga.md)

## 当前实现状态

State Plane 已具备真实 SQLite/WAL、短事务 CAS、operation 幂等、依赖边原子提交、语义事件冲突检测、内容寻址 Artifact、Doctor、保守 GC、可验证备份/恢复，以及 Community 5.18 `.ai` 状态的验证式单向导入。Legacy Inventory 将受支持事实导入State Plane，将非权威脚本、文档和工具惰性原地保留；只有可能污染Identity、Migration Marker或State Authority的未知内容才阻断。Project Bootstrap 使用同一只读 Preflight 识别新项目、旧版项目、活动项目、移动、显式 Fork、身份冲突、损坏状态和未完成迁移；Project ID 不再由路径推导，普通读命令不会创建 UUID。所有生产状态变化收敛到 `StateCoordinator`，测试会阻断新的平行写入口。

Runtime 已建立 provider-neutral Adapter contract、Task/Agent/Lease/Ownership/Worktree 同代提交、PID+启动时间+可执行身份验证、最小交接包、崩溃恢复、两阶段资源释放、真实 Git Worktree、风险自适应冲突调度、持久化 Merge Queue 和 Codex CLI Adapter。低/中风险最多三个互不重叠 Writer Lane；高/关键风险收敛到一个 Writer，并要求独立 Assurance Lane。Merge Queue 只授权已冻结、已验证且仍基于目标 HEAD 的候选，不静默执行合并。

Environment & Resource Control Plane 使用可扩展 Provider 字符串、能力图、版本化 Profile、Task 级 Instance、通用 Resource Lease 和精确 Process Registry 管理本机直接环境、隔离工作区及组合 Provider。核心没有 `Native / Docker / Hybrid / Remote` 模式枚举，也不按 Provider 产品名分支。环境发现不产生选择状态；Verified Profile 才能被 Task 绑定。Runtime Start、Handoff、Crash Recovery 和 Completion 会验证或转移同一 Environment Instance，恢复前重新核验真实资源，终态先释放环境再归档 Writer 事实。已接管的外部环境与进程只解除 Hiker 绑定，不会被停止或删除。

Live Adoption 通过 `attach` / `checkpoint` / `resume` 在自然 Turn 边界把 Community 5.18 当前 Goal、Task 与有效 Checkpoint 绑定到同一 5.19 Project；Provider Session 仅保存指纹。普通 `attach` 会先执行确定性 Preflight，并在同一既有 Project Identity 下自动完成安全 Legacy migration、已知旧运行态 Finalize、Doctor 复检和 Adoption；普通调用方不需要提供 migration operation ID、Project ID、Goal ID 或 Task ID。接管事务不会恢复旧 Lease、Agent、Process 或重复创建 Task/Worktree，后续 Context 只从有界活动索引解析。Community 侧保持薄检测、Bootstrap 与 fallback，Pro Runtime 仍是状态和恢复权威。

CLI 的 `--json` 模式使用 `hiker-cli/v1` 单文档 Envelope：stdout 只包含一个主 JSON，stderr 只包含非 JSON 诊断，Envelope 内 `exit_code` 与进程退出码一致，并明确报告 `PRO_ACTIVE / PRO_DEGRADED / COMMUNITY_FALLBACK / PRO_REQUIRED_BLOCKED`。稳定 Launcher 从原子 Runtime Pointer 选择不可变版本目录；更新只切换 Pointer，不替换正在运行的进程或项目 `.ai`。

Change Intelligence 复用现有 `ImpactGraphEngine` 和 `StateCoordinator`：LOCAL 变更走有界、零写快路径；共享、跨域和系统性变更才要求不可变 Change Contract、完整 Blast、单 Writer/Assurance/回滚策略。连续失败会进入 Fix Loop 和 Root Cause Mode，临时机制必须登记 Patch Debt，Scope/Invariant Guard 阻断越界与回归。观察结果只形成校准证据，不会自动污染权威依赖图。

Context Resolver、`hiker resume`、结构化 Decision Memory、Intent/Dependency/Evidence Graph、结构化 Goal Change、50 服务语义拓扑、资源租约、SQLite 查询计划与并发证据、签名 Entitlement、设备激活、Offline Grace、签名 Module/Update、默认拒绝降级和签名回滚授权均已实现。Decision Memory 保留已拒绝方案及原因，并原子替代当前决策；Goal Change 接受模型给出的 `AFFECTED / UNAFFECTED / SUPERSEDED / REQUIRES_REVIEW` 分类，但只由 Runtime 验证 Task/Evidence 绑定并在一个 CAS 事务内提交 Goal Revision、Task 绑定和必要的证据失效。两者都不使用关键词替代语义判断。

验证工具已覆盖：

- 12 个月加速老化：1201 Task、120 Goal Revision、120 Handoff、60 Crash；
- 1/2/4/8 Worker 的真实 SQLite/WAL CAS 竞争，以及基于生产 Runtime Contract 的并发 Fake Adapter 启动/释放；
- 100k 非空语义文件与真实 Git add/commit/delta；
- 5 个隔离可执行 HTTP 服务；
- PostgreSQL 16.14、100k 行 before/after 与 8 客户端并发；
- COMM-G1/G2/G3/G16/G17 公私边界和热路径成本；
- 状态、Handoff、Artifact、Migration、Release 的故障注入。
- Environment Profile/Instance、资源冲突与容量、隔离工作区、跨 Task 进程保护、Provider Recovery/Release，以及冷历史有界读取。
- Change Risk Probe、Change Contract/Blast、Fix Loop、Root Cause、Patch Debt、Scope/Invariant Guard、Outcome Calibration，以及真实迁移历史影子回放。

这些结果分别属于 `DETERMINISTIC_VERIFIED`、`SIMULATION_PROVEN` 或 `REAL_INTEGRATION_PROVEN`；不等于客户现场长期运行。当前是发布候选，最终状态只由同一提交生成的商业 Gate 决定。

Community 5.18 导入的运行控制事实不会被激活为 5.19 Runtime Writer、Lease、Agent 或 Worktree。普通 `attach` 会自动调用同一内部收敛链；`hiker migrate-community` 与 `hiker finalize-legacy` 仅保留为高级诊断/恢复命令。Finalize 会先创建可验证备份，再用单个 SQLite 事务将已知 schema 2.0.0 的旧 Task/Checkpoint/Ownership/Worktree/Lease/Agent 事实转为只读 `Archived`；payload、authority、schema 与 fingerprint 保持不变。普通未知脚本或文档惰性原地保留，疑似权威状态、未知 schema、reparse/path escape、损坏或超出盘点预算的数据确定性阻断。

## 当前验证结果

以下为 2026-08-27 的本地可重复证据；原始机器相关 JSON 保存在被 Git 忽略的 `artifacts/verification/`。

| 验证面 | 结果 | 证据等级 |
| --- | --- | --- |
| Unit / Integration | rc.16 源码回归 254/254 PASS，新增完整 Codex 宿主重启后的旧 Provider Binding 确定性收敛，并保留活动/无法证明场景的失败关闭；最终数量仍以同提交商业 Gate 的 TRX 为准 | `DETERMINISTIC_VERIFIED` |
| Community 回归 | 公开候选 `b29c7ca` 五插件 350/350 PASS；完整发布与隔离安装 Gate PASS；候选仍为 Draft，未合并 `main` | `DETERMINISTIC_VERIFIED` |
| Environment Control | 本机直接接管、真实隔离目录、组合 Provider、Runtime/Handoff/Recovery/Release 通过；1,024 条冷历史后 Context/Doctor P95 保持有界 | `REAL_INTEGRATION_PROVEN` |
| Change Intelligence | LOCAL 零写快路径、共享 Blast、1024 条冷历史有界 Context、最近 3 个真实迁移修复提交的 Fix Loop 影子回放通过；真实 Field 返工改善尚未证明 | `REAL_INTEGRATION_PROVEN` |
| 加速老化 | 12 个合成月份、1201 Task、120 Goal Revision、120 Handoff、60 Crash | `SIMULATION_PROVEN` |
| 并发写入 | 1/2/4/8 Worker 均恰好一个 CAS 成功；Runtime Worker 结果需由当前提交重跑确认 | `SIMULATION_PROVEN` |
| 大仓库 | 100,000 个非空语义文件；100 文件增量 `git status` 269.969 ms | `SIMULATION_PROVEN` |
| 服务集成 | 五个真实进程、唯一端口与 HTTP Health | `REAL_INTEGRATION_PROVEN` |
| PostgreSQL | 16.14、100,000 行；查询 P95 9.528 ms → 0.114 ms；八客户端零错误 | `REAL_INTEGRATION_PROVEN` |
| 商业边界 | 私有仓库可见性、Public Source / ZIP Pro 泄漏、私钥与本机路径扫描通过 | `DETERMINISTIC_VERIFIED` |
| License | 250 次签名验证 P95 1.649 ms；StateStore 热路径 License 检查为零 | `DETERMINISTIC_VERIFIED` |
| Codex Runtime | 真实 `codex-cli 0.150.0-alpha.8` 完成隔离 Worktree、Checkpoint、同 Session Recover 与释放 | `REAL_INTEGRATION_PROVEN` |

Context 在加速老化第 12 月仍为 548 bytes、五次状态读取、零次冷历史扫描；State Query P95 0.024 ms，Context Build P95 0.136 ms，Resume P95 3.310 ms，Affected Scope P95 0.172 ms。

Environment Release 专项验证中，加入 1,024 条冷环境历史前后，Task Context P95 为 0.291 ms → 0.336 ms、Environment Doctor P95 为 1.155 ms → 0.672 ms；状态读取保持 7 次，Context 保持 3,917 bytes。相同 Verified Profile 与 Active Instance 复用时 generation 保持 22 → 22，不产生额外状态写入。数值是本机证据，不外推为生产 SLA。

Change Intelligence 专项验证中，LOCAL Probe P50/P95 为 0.0354/0.0465 ms，共享 Blast P50/P95 为 0.0939/0.3313 ms；加入 1,024 条冷 Change 历史前后，Context 都是 6 次状态读取、4,258 bytes。数值是本机证据，不外推为生产 SLA；影子回放不能替代真实 Field 的 Time To Accepted Change 指标。

真实客户项目和真实日历 12 个月仍为 `NOT_YET_FIELD_PROVEN`；多 Provider Agent、PostgreSQL Pool Wait / Lock Wait / Saturation Point，以及宿主 Token 节省百分比仍为 `NOT_MEASURED`。

## 安装包使用

`hiker-pro-<version>-win-x64.zip` 是签名的目录式自包含发行包。目标 Windows x64 电脑不需要安装 .NET SDK 或 .NET Runtime，也不依赖全局 `DOTNET_ROOT`。不得只复制包内某个 `hiker.exe`，也不得把新 DLL 覆盖到正在运行的旧目录。

首次安装必须使用独立取得并已信任的 Release 工具、公钥及公钥 SHA-256；包内 `payload/tools/hiker-pro-release.exe` 只有在整个包通过签名与哈希校验后，才可作为后续升级工具。安装仅在 Codex Turn 终态/自然 Checkpoint 后执行：

```powershell
hiker-pro-release verify `
  --package <hiker-pro.zip> `
  --public-key <release.public.pem> `
  --key-id <release-key-id> `
  --public-key-sha256 <pinned-sha256>

hiker-pro-release install `
  --package <hiker-pro.zip> `
  --public-key <release.public.pem> `
  --key-id <release-key-id> `
  --public-key-sha256 <pinned-sha256> `
  --home D:\Hiker\5.19 `
  --update-user-path
```

安装器先验证外部 checksum、固定 Trust Root、签名清单和全部 Artifact Hash，再把 Runtime 提升到不可变版本目录并原子切换 Pointer。已有平铺 Runtime 会先做有界探测、逐文件 Hash 和不可变快照，作为 `previous_runtime` 保留；不会删除项目 `.ai`，也不会扫描项目、Git 或聊天历史。`bin/hiker.exe` 是稳定入口，根目录 `hiker.exe` 只作为旧 PATH 的薄兼容入口；机器 Locator 位于 `%LOCALAPPDATA%\Hiker\pro-runtime.json`，供 Community Bridge 在 Codex 不重启时有界发现稳定入口。

显式回滚只切换到 Pointer 中上一个已验证 Runtime，不恢复旧 Lease、Agent、Process 或项目运行态：

```powershell
D:\Hiker\5.19\tools\hiker-pro-release.exe runtime-rollback --home D:\Hiker\5.19
```

```powershell
hiker version --json
hiker doctor --root <project>
hiker status --root <project>
hiker backup --root <project>
hiker export --root <project>
```

Community Bridge 在宿主提供 Provider Session 与安全边界后调用 `attach`；该命令会自动分流新项目、已激活项目、安全 Legacy 项目和半迁移项目，普通用户不需要选择 `init / migrate-community / finalize-legacy` 的顺序，也不需要生成 operation ID、Project ID、Goal ID 或 Task ID。`init`、`migrate-community` 与 `finalize-legacy` 只保留为高级诊断/恢复命令；所有入口仍经过同一个 Project Preflight。已有项目会从唯一权威状态自动解析原 ID，身份冲突、未知 schema、损坏或疑似权威冲突会结构化阻断而不会生成第二个 UUID。

显式 Fork 必须提供新的稳定 ID，并在原子事务中继承可复用事实、清除运行中 Task/Lease/Ownership/Worktree 绑定：

```powershell
hiker fork --root <copied-project> --project-id <new-stable-id>
hiker restore --root <project> --backup <file> --confirm-sha256 <sha256>
```

运行时项目状态只写入目标工程自身的 `.ai`；这些命令不会安装或替换 Codex 插件。Legacy 导入保留原始 `.ai` 文件，不默认删除、清空或重建。

## 源码开发

只有从源码编译和运行测试需要 .NET 9 SDK：

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

## 仓库结构

| 路径 | 职责 |
| --- | --- |
| `src/Hiker.Pro.Runtime/State` | SQLite/WAL 权威状态、CAS、事件、Artifact 元数据、Backup/Restore/Export |
| `src/Hiker.Pro.Runtime/Environment` | Provider Contract、Profile/Instance、Resource/Process Lease、Doctor、Recovery 与 Release |
| `src/Hiker.Pro.Runtime/ChangeIntelligence` | Change Risk、Contract/Blast、Fix Loop、Patch Debt、Scope/Invariant Guard 与 Outcome Calibration |
| `src/Hiker.Pro.Runtime` | State、Task、Lease、Worktree、Checkpoint、Recovery、Context、Graph 与商业信任边界 |
| `src/Hiker.Pro.Cli` | 面向工程项目的薄 CLI 与 `hiker-cli/v1` machine contract |
| `src/Hiker.Pro.Activation` | 验证式 Runtime staging、不可变版本目录、原子 Pointer、兼容性与回滚 |
| `tools/Hiker.Pro.Release` | 签名发布、完整性验证、稳定 Launcher 安装、平铺 Runtime 引导与原子激活 |
| `src/Hiker.Pro.Launcher` | 稳定入口，只从活动 Pointer 解析并启动当前 Runtime |
| `tools/Hiker.Pro.Verification` | 老化、规模、真实服务、Codex Runtime、Environment、Change Intelligence 与 COMM-G1–G17 验证 |
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
dotnet run --project tools/Hiker.Pro.Verification -c Release -- --scenario environment-control --root .
dotnet run --project tools/Hiker.Pro.Verification -c Release -- --scenario change-intelligence --root .
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

`verify` 的 Clean Install 在隔离临时目录中执行，不安装或替换当前 Codex 插件。验证会清空子进程 `PATH`、将 `DOTNET_ROOT` 指向不存在目录并禁用 multilevel lookup，证明 `hiker.exe` 使用包内 Runtime；缺少任一必需 Runtime 文件都会由 `SELF_CONTAINED_RUNTIME` 阻断。Gate 的唯一通过结果是 `PASS_5_19_COMMERCIAL`；任一事实缺失或不匹配均返回 `BLOCKED_5_19`。

完整签名构建、独立信任根和聚合 Gate 说明见 [5.19 Release Process](docs/release/5.19-release-process.md)。私钥、签名密钥和真实商业 License 禁止进入 Git、Fixture、日志或发行包。
