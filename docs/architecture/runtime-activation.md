# Stable Runtime Activation

Hiker 5.19 Pro 将稳定命令入口与可更新 Runtime 分离：安装后的 `bin/hiker.exe` 是薄 Launcher，实际 Runtime 位于不可变的 `runtimes/<runtime_identity>/`。活动版本只由 `state/current-runtime.json` 指定；Launcher 日常只读取 Pointer 和对应 Runtime Identity，不扫描版本目录、项目 `.ai`、Git 或历史 Evidence。根目录兼容 Launcher 使用同一薄实现，用于仍缓存旧 PATH 的 Codex 进程；Community 也可从单个有界机器 Locator 解析 `bin/hiker.exe`。

## 激活事务

候选 Runtime 必须先在 staging 完成有界文件盘点、逐文件 SHA-256、包签名/Entitlement 状态和 `hiker version --json` 能力探测。Runtime API 或 Live Adoption Protocol 不一致时失败关闭；State Schema 变化还必须携带绑定可验证备份 Hash 的兼容 receipt。验证通过后文件被提升到不可变版本目录，最后才原子替换活动 Pointer。

Pointer 提交前失败不会改变当前活动 Runtime；提交后旧进程继续运行原二进制，新进程从新 Pointer 启动。Launcher 持有共享 Runtime-use Lock，激活器必须取得独占 Lock，因此实际 CLI 调用尚未终态时不能切换。Pointer 保留上一个已验证 Runtime，可用同一管理器回滚；回滚不恢复旧 Lease、Agent、Process 或项目 Runtime State。

旧式平铺安装只允许一次确定性引导：从可执行文件版本事实证明 Git commit，通过 `hiker version --json` 验证协议事实，对顶层自包含文件做有界 SHA-256 清单并复制到不可变 Runtime 目录。原目录不删除；新 Runtime 激活后该快照成为 `previous_runtime`。若旧版本身份、文件完整性或边界不能证明，引导失败关闭。

Release 安装器使用外部 checksum、独立公钥、固定公钥指纹、ECDSA P-256 签名清单和逐 Artifact SHA-256。ZIP entry 数量、解压体积、Runtime 文件数、基础设施文件数、路径穿越、符号链接和 reparse point 均有确定性边界。包内更新器只有在整包验证后才进入受信安装目录，不作为首次验证的 Trust Root。

## 运行身份

Launcher 将 `runtime_identity`、build fingerprint、install generation、activation epoch 和 channel 作为受限环境事实传给 Runtime。State Store 的统一 Event 写入口把身份加入 operation event；Receipt 可证明某个状态变化由哪个已激活 Runtime 产生，但不会记录本机路径、完整 Prompt、聊天或源码。

## 热路径预算

正常启动只读取两个小文件并启动一个子进程；项目路由使用有界 `project-facts` 活动索引，冷历史只在显式诊断时读取。更新、逐文件 Hash、包验证和兼容性检查只在显式激活路径执行，不进入每 Turn、每状态写或普通 Community fallback。
