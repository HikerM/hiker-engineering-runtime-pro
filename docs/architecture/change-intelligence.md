# Change Intelligence

`GA-5.19-CHANGE-INTELLIGENCE-001` 在既有 `ImpactGraphEngine` 上增加变更风险、契约、Fix Loop、Patch Debt、Invariant Guard 与结果校准；它不建立第二套工程图或第二个状态写入口。

## 风险与执行边界

`ChangeRiskProbe` 只读取当前有效依赖边、资源冲突和当前 Change Loop。孤立局部变更走 `LOCAL` 快路径，最多执行有界的一跳查询、零状态写入，也不会运行完整 Blast 分析。`SHARED / CROSS_CUTTING / SYSTEMIC` 才要求结构化 `ChangeContract` 并调用既有 `ImpactGraphEngine`。

风险结论同时考虑受影响范围、共享中心性、数据/运行时/环境/资源/发布维度，以及关系的来源、指纹、置信度、新鲜度和权威性。Git 共变只作为 `RISK_SIGNAL_ONLY`，不能自动提升为权威依赖。

高风险执行策略可要求单 Writer、独占变更、独立 Assurance 和回滚边界。`ConflictAwareScheduler` 消费同一个 `ChangeExecutionPolicy`，不按语言、框架或 Provider 名称分支。

## 收敛与证据

`ChangeLoopCoordinator` 只保留最近八次结构化尝试。连续三次失败并出现范围扩张、fallback、special case 或 Evidence 失效时，状态进入 `FIX_LOOP_CONFIRMED`，普通补丁被禁用，下一步必须提交 Root Cause Plan。同一不可变 Change Contract 可以用新的 operation 重新分析，但不会复制 Contract 或绕过 operation 幂等。

临时 fallback、dual-write、compatibility shim 等必须登记 `PatchDebt`，包含移除条件、复核条件、影响范围和 Evidence。债务解决后退出活动索引，历史事实仍可审计。Scope Guard 会识别越界范围、新共享事实和未登记的临时机制；Invariant Guard 将真实回归与 flaky、环境、资源或未知失败分开。

`ChangeOutcomeCoordinator` 保存预测/观察差异、返工次数、Evidence invalidation、缺失边和校准发现。Outcome 只能产生后续校准证据，不能自动改写工程图权威关系。

## 状态与上下文

所有 Change 状态都通过 `StateCoordinator` 的 CAS/operation journal 写入。Contract 与预测 Impact 原子提交，并直接关联当前 Task。`ContextResolver` 只有在当前 Task 存在该关系时才读取活动 Change Contract/Impact；冷历史不会因项目年龄自动进入上下文。

状态中只允许有界语义标识、指纹、时间和度量，不保存完整 Prompt、聊天、源码、密钥或开发者绝对路径。

## 验证边界

确定性测试覆盖局部/共享/系统性变更、API 消费者、共享数据库资源、不可逆迁移、三次失败 Fix Loop、Patch Debt、Scope Breach、Invariant Regression、非权威 Git 共变、关系校准、幂等和冷历史有界读取。

显式 `change-intelligence` 场景使用仓库中的真实源码指纹和最近三个 Legacy Migration 修复提交做影子回放。该证据证明执行机制与历史信号可工作，但不等于已经证明真实 Field 的返工率或 Time To Accepted Change 改善。
