# Community / Pro Commercial Isolation Boundary

## 仓库边界

| 范围 | Repository | Visibility | 允许内容 |
|---|---|---|---|
| Community 5.18 | `HikerM/hiker-workflow-skill-pack` | PUBLIC | 现有5插件、42 Skill、脚本、Schema、测试、安装器和5.18发布包 |
| Pro 5.19 | `HikerM/hiker-engineering-runtime-pro` | PRIVATE | 七域商业Runtime、商业测试与商业发行工具 |

GitHub Metadata已于2026-08-26验证Pro仓库：`private=true`、`visibility=PRIVATE`、默认分支`main`。

## 必须留在Community的能力

- ChatGPT/Codex语义路由与最多两个Skill懒加载；
- 5.17 Control Kernel、Goal/Task/Checkpoint、Crash Recovery、Event Budget、Worktree/Ownership；
- 5.18产品质量、B/S、C/S、Adaptive Governance与Self Governance；
- Community安装、包、文档和公开兼容协议。

## 只允许存在于Pro的实现

- StateStore/SQLiteStateStore、State Coordinator、Migration、Advanced Doctor与GC；
- Runtime Contract、Provider Adapter、Scheduler、Handoff与Recovery Engine；
- Context Resolver、Semantic Memory与Intent/Dependency/Evidence Graph；
- Service Topology、Resource Lease、Data Performance Engine；
- Signed Entitlement、Activation、Offline Grace、Feature Boundary、Module/Update Trust；
- 商业Benchmark、老化、并发、故障注入和商业Release Gate。

## 集成边界

Pro可以读取Community版本、公开Schema与公开CLI结果，但不得把Community仓库变成Pro源码托管或隐蔽下载器。Community在没有Pro时继续完整工作。Pro失效或License失效时，用户仍可读取、备份、导出和恢复`.ai`，并继续使用Community能力。

Community→Pro 接管使用显式能力握手，而不是只比较产品版本号。Community 仅调用 `hiker version --json` 并要求当前 `live_adoption_protocol`；不支持该握手或协议版本不匹配的旧 RC 必须静默回退 Community，不能尝试写 State。Community 不包含 Goal/Task/Checkpoint、CAS、Ownership、Recovery 或 Context Resolver 的 Pro 实现。

## 密钥边界

- 发行签名私钥和License签名私钥只存在于外部受控签名环境；
- Client/仓库/Fixture只包含公钥或运行时生成的临时测试密钥；
- `.gitignore`阻断常见私钥格式；Release Gate还必须扫描内容和归档；
- 任何真实私钥泄漏均为`COMM-G3 BLOCKED`。

## 防泄漏判定

`COMM-G1`与`COMM-G16`必须同时验证：Public Git树、Public发行ZIP和Public Git历史不包含Pro模块名、Pro命名空间或商业实现指纹。仅检查当前工作树不够。
