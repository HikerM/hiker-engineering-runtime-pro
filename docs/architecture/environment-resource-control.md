# Environment & Resource Control Plane

`GA-5.19-ENVIRONMENT-001` 管理工程 Task 实际依赖的本机环境、隔离环境、进程和有容量资源。它控制资源真实性和所有权，不规定 Agent 的设计、诊断或实现路径。

## 开放模型

核心没有 `Native / Docker / Hybrid / Remote` 模式枚举。Provider ID、Capability、Action、Resource Type 和 Purpose 都是有界字符串；新增 Provider 实现同一 Contract，不修改 Scheduler、StateStore 或 Runtime 状态机。唯一闭合集合是资源共享语义：`SHARED / EXCLUSIVE / ISOLATED / CAPACITY`。

Provider Contract 提供可选的 `Detect / Inspect / Validate / Plan / Attach / Allocate / Provision / Execute / Observe / Health / Recover / Release`。未实现能力返回结构化不兼容，而不是由核心按产品名猜测。`SyntheticFutureProvider` 回归证明未知未来 Provider 可直接注册并完成选择、激活与资源租约。

## 权威事实与生命周期

```text
bounded discovery (no state write)
→ VERIFIED Environment Profile
→ SELECTED version + fingerprint
→ Task-scoped PREPARING Environment Instance
→ provider adopt/allocate/provision
→ concrete Resource Lease
→ ACTIVE Environment Instance
→ Runtime Start / Handoff / Recovery
→ provider release or adopted-resource detach
→ ARCHIVED Instance / Lease / Process registration
```

Profile 使用稳定的 `project + purpose` ID、单调 Version 和环境 Fingerprint。工具链或显式 Manifest 变化会把当前 Profile 标记为 `STALE / ENVIRONMENT_DRIFT`；重新验证后在同一权威实体上增加 Version。Instance 同时绑定 Profile Version/Fingerprint，旧 Instance 不能静默使用新 Profile。

Instance 由 Task 持有；Goal ID 只是可追溯绑定，不建立 Goal→Instance 所有权。一个 Task 同时只能存在一个活动或待恢复 Instance。所有状态变化通过 `StateCoordinator` 和 operation journal 提交；同一 operation 重试只读取既有事实。Provider 外部动作与 SQLite 不能伪装成分布式事务，因此动作前写 `PREPARING`，动作或提交不完整时明确进入 `RECOVERY_REQUIRED`，禁止盲目重放。

## 资源与调度

`ResourceRequirement` 不内置端口、数据库、GPU、设备或容器类型。Provider 把逻辑需求解析为具体 Resource ID，核心只执行通用冲突策略：

- 兼容共享资源可并行；
- Exclusive/Isolated 活动占用返回 `WAITING_RESOURCE`；
- Provider 可为支持隔离的冲突返回 `AUTO_ISOLATED` 并分配新的具体 ID；
- 容量不足返回 `CAPACITY_EXHAUSTED`；
- 共享语义或破坏性要求不兼容返回 `RESOURCE_CONFLICT`；
- 破坏性资源只允许 `EXCLUSIVE / ISOLATED`。

Scheduler 同时考虑代码 affected scope、dependency scope、资源、Environment Profile/Instance、现有 Runtime writer capacity 和 engineering risk。高/关键风险仍要求独立 Assurance；核心没有 Provider 产品分支。

## Process、恢复与释放

Process Registry 只保存 PID、process start time、executable fingerprint 和 host fingerprint，不保存命令行、环境变量或 Secret。终止授权必须同时匹配 Task、Runtime 和精确活进程身份。已接管的外部进程永不由 Hiker 终止；PID 复用、身份漂移和跨 Task 请求都拒绝。

Runtime Start 必须验证 Profile、Instance、Version、Fingerprint、Worktree、Runtime 和全部 Lease。Handoff 与 Crash Recovery 转移同一个 Instance/Lease/Process registration；Recovery 在启动替代 Runtime 前调用 Provider 重新验证真实资源。Completion 先释放环境，再归档 Writer Lease/Ownership/Worktree。已接管资源只解除本地租约，Provider 不停止外部服务；Hiker 创建的隔离目录只有在 resource/task/runtime Marker 全部匹配时才可删除。

## 热路径与隐私

发现只读取固定 Manifest（当前 Local Provider 最多五个显式路径），不扫描仓库、Git 历史或全部 `.ai`。Context Resolver 只沿当前 Task 的 Profile/Instance/Lease/Process 依赖边读取。Environment Doctor 只读取活动索引，冷归档只在显式历史操作中访问。

环境状态禁止 Secret 值、Token、Credential、带认证信息的 URI、完整 Prompt、聊天或源码。允许 Secret Reference 与 fingerprint。项目绝对路径不会进入 Profile、Instance、Event 或 Context；Local Provider 只保存 root fingerprint。

`environment-control` 验证场景加入 1,024 条冷历史后，Context 的读取次数和 bytes 不变，Profile/Instance 复用零写入。所有环境检测、验证、测试和证据只在受治理 Task 按需启用；没有 Environment 绑定的既有 Runtime 路径不新增状态读取、工具调用或默认 Prompt/Token。
