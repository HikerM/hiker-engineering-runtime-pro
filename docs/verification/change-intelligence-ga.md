# GA-5.19-CHANGE-INTELLIGENCE-001

## CURRENT_BASELINE

保留唯一 `ImpactGraphEngine`、`GoalChangeCoordinator`、typed dependency edges、Evidence invalidation、Service Topology、Environment/Resource State、`ConflictAwareScheduler`、Decision Memory 与 `StateCoordinator`。未创建第二套工程图、状态库或 Writer。

## CHANGE_RISK_PROBE

`LOCAL / SHARED / CROSS_CUTTING / SYSTEMIC` 由当前有效依赖、中心性、风险信号、资源和 Change Loop 确定。LOCAL 最多 16 个 seed、每个 seed 64 条有效一跳边加一个预算哨兵，零状态写、零完整 Blast。

## CHANGE_CONTRACT

高风险变更要求不可变 Contract 显式绑定 `project_id / change_id / task_id / goal_revision / source_fingerprint`、允许范围、预期变化、不变量、验收与回滚边界。Contract 和预测 Impact 原子提交；相同 operation 幂等，相同不可变 Contract 可在新 operation 下重新分析。

## BLAST_RADIUS_MODEL

完整分析复用 `ImpactGraphEngine` 的 bounded traversal。权威依赖传播为 `AFFECTED`，模糊与历史共变为 `REQUIRES_REVIEW`；截断升级为 `SYSTEMIC`。

## MULTI_DIMENSION_IMPACT

覆盖 Structural、Contract、Data/State、Runtime、Resource、Verification、Temporal、Security、Performance、Delivery 和 Historical。维度输入有界并执行敏感数据拒绝。

## CENTRALITY

记录直接/传递消费者、Contract/Service/Evidence/Runtime/Resource fanout、Critical Path 与历史共变度；风险由传播面决定，不由修改行数决定。

## RELATION_CONFIDENCE

每条关系记录 type、fingerprint、source、confidence、freshness、authority、observed time。Git 共变只标记 `RISK_SIGNAL_ONLY`，不能自动成为权威依赖。

## TEMPORAL_IMPACT

迁移兼容窗口进入 Temporal dimension；不可逆数据库/安全/交付关键变更升级为 SYSTEMIC，并要求显式 rollback boundary。

## FIX_LOOP_CONTROL

有界保留最近 8 次尝试。连续 3 次失败且出现范围扩张、fallback、special case 或 Evidence stale 时进入 `FIX_LOOP_CONFIRMED`，禁用普通补丁。

## ROOT_CAUSE_MODE

生成结构化 symptom、hypothesis、shared invariant、冲突约束、失败历史、拒绝方案、当前项目 dependents、验证计划和 rollback boundary。跨项目依赖失败关闭。

## PATCH_DEBT

fallback、dual-write、compatibility shim 等必须登记 Debt、移除/复核条件、影响范围与 Evidence。解决后退出活动索引但保留历史；Debt 不能绑定另一个 Change 的 Contract。

## INVARIANT_GUARD

主验收通过不能掩盖已有行为回归。Regression 与 flaky、environmental、resource conflict、unknown 分开；缺失 Evidence 和开放 Patch Debt 均不能 ACCEPT。

## REGRESSION_DERIVATION

回归集合由 Contract acceptance、expected-unaffected behavior 和 Blast 中的 Test/Evidence consumer 合成，最多保留有界集合。

## SCHEDULER_INTEGRATION

Scheduler 消费 `ChangeExecutionPolicy`，系统性变更优先、单 Writer、独占 mutation、独立 Assurance；Fix Loop 禁止普通 Writer。无语言、框架或 Provider 产品名分支。

## ENVIRONMENT_RESOURCE_INTEGRATION

Risk Probe 使用通用 `ResourceRequirement` 和现有 Environment/Resource edges；破坏性独占资源会升级治理。没有复制 Environment Control Plane。

## GOAL_CHANGE_INTEGRATION

Blast、confidence、dimensions 与 Evidence 通过当前 Task 的状态边进入有界 Context，供模型提交 Goal Change 的结构化分类；Runtime 仍只验证 `AFFECTED / UNAFFECTED / SUPERSEDED / REQUIRES_REVIEW`，不以关键词替代语义。

## CONTEXT_COST

Change Contract/Impact 仅通过当前 Task 关系读取。加入 1,024 条冷 Change 历史前后均为 6 次状态读取、4,258 bytes；不读取完整聊天或历史 Turn。

## PREDICT_OBSERVE_CALIBRATION

Outcome 必须与 State Plane 中的权威预测 Blast 完全一致，调用方不能替换预测。观察差异只形成 calibration finding，不自动改写依赖图。

## OUTCOME_TELEMETRY

记录 affected/unknown node、fix attempts、Patch Debt create/remove、regression、false positive/missing edge、rollback、acceptance 与 time-to-accepted；不记录 Prompt、聊天、源码、密钥或开发者绝对路径。

## PERFORMANCE

本机显式场景：LOCAL Probe P50/P95 `0.0354/0.0465 ms`，共享 Blast P50/P95 `0.0939/0.3313 ms`；预算分别为 5/10 ms。LOCAL generation `2→2`，零状态写。数值不外推为生产 SLA。

## TESTS

- Change 定向：32/32 PASS。
- 全量 Debug：172/172 PASS。
- 全量 Release：172/172 PASS。
- Change Integration Scenario：4/4 PASS。
- Commercial Boundary：PASS。
- Commercial Security COMM-G4–G15：PASS。
- `dotnet format --verify-no-changes`：PASS。

## REAL_FIELD

使用真实源码指纹和最近 3 个 Legacy Migration 修复提交完成有界影子回放，识别 1 次范围扩张并在第三次失败后阻断第四次普通补丁。这证明机制可作用于真实历史，但不是无 Change Intelligence 与有 Change Intelligence 的真实开发对照。当前：

- `field_outcome_improvement_proven=false`
- `time_to_accepted_change_improvement_proven=false`

## VERDICT

`BLOCKED_CHANGE_INTELLIGENCE`

唯一未关闭 Gate 是真实项目对照尚未证明返工率下降与 Time To Accepted Change 改善。实现、正确性、性能、上下文、公私边界和安全 Gate 均已通过；不得把影子回放升级为 Field 成效声明。
