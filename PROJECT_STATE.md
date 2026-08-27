# Hiker 5.19 Pro Project State

- Product: `5.19.0-rc.4`
- Runtime: `0.1.0`
- State Schema: `3.0.0`
- Release branch requirement: `main`
- Repository: `HikerM/hiker-engineering-runtime-pro`
- Visibility requirement: `PRIVATE`
- Phase: `COMMERCIAL_RELEASE_CANDIDATE`
- Field proof: `NOT_YET_FIELD_PROVEN`
- Active release blocker: rc.4 仍需在干净且与 `origin/main` 一致的发布提交上重建签名 ZIP、完整证据和商业 Gate；当前功能分支证据不能替代正式发布 Gate。

本文件记录候选身份，不静态宣称发布 Gate 已通过。最终状态必须读取同一源码提交生成的本地 `artifacts/verification/commercial-release-gate.json`；该文件被 Git 忽略，避免把机器路径、运行时间或一次性签名证据提交到源码历史。

发布候选必须同时满足：

- 当前 `main` 与 `origin/main` HEAD 一致且工作区干净；
- Public Community 5.18 基线保持 `05e94e32af719c81f740fe2af08c35f98a527588`；
- Unit / Integration、加速老化、大仓库、真实服务、真实数据库、真实 Codex Runtime 与商业 Gate 全部绑定当前提交；
- RC ZIP 通过外部 SHA-256、独立信任根、签名 Manifest、逐文件 Hash、隐私、Clean Install 与 Tamper Gate；
- COMM-G1–G17 全部 PASS；
- 真实客户长期运行继续标记为 `NOT_YET_FIELD_PROVEN`，不得由模拟证据替代。

发布流程见 [5.19 Release Process](docs/release/5.19-release-process.md)。
