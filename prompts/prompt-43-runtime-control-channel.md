# prompt-43：运行时控制通道 —— 让运行中的 ClipShelf 可被脚本驱动

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。
前置：prompt-31（测试底座）、prompt-32（历史上限可调）已交付。
**来源**：非 Windows 交接清单，由用户 2026-09-27 直接提出（「需要先加一个 ClipShelf 运行时也能测试」）。纳入理由：现有端到端验证只能靠「往系统剪贴板写字」驱动，逼得用户每次测试前都要退出自己日常在用的 ClipShelf。

## 1. Goal

让外部脚本能**驱动一个正在运行的 ClipShelf 实例**，从而在真实运行路径（轮询 → 采集 → 去重 → 裁剪 → 落盘）上做端到端验证；同时保证该能力**完全不干扰用户日常使用**——驱动过程既不读写系统剪贴板，也不触碰用户真实数据目录与标准偏好域。

具体地：运行中的实例能接受来自同机脚本的命令（注入剪贴板内容、导出内存中的记录、读取状态、修改历史上限、清空历史、退出），并把结果以机器可读形式回给脚本。

## 2. Evidence

**实测环境**：2026-09-27，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`，HEAD `355acec`（工作区另含 prompt-31、prompt-32 的未提交改动）。

- 现有隔离接缝**只有两个**：`Support/AppEnvironment.swift` 的 `CLIPSHELF_DATA_DIR`（数据目录）与 `CLIPSHELF_DEFAULTS_SUITE`（偏好域）。
- **剪贴板没有接缝**：`Stores/ClipStore.swift:19` `private let pasteboard = NSPasteboard.general` —— 硬编码系统剪贴板。
- 现有 CLI **只有 `--self-test <报告路径>` 一个模式**：`Support/SelfTest.swift` 的 `SelfTestCommand.runIfRequested()`，由 `Sources/ClipShelfLite/main.swift` 在启动 `ClipShelfApp.main()` 之前调用。
- 外部黑盒套件 `tests/parity/lib.sh` 通过 `set_clipboard_text` / `pbwrite.swift` **写系统剪贴板**来驱动被测应用；因此 `lib.sh:122-127` 有前置守卫：检测到已有 `ClipShelf` 进程即 `exit 2`，要求用户先退出。
- 现有 `--self-test` 只覆盖**纯逻辑**（`HistoryLimitPreferences` 规整、`HistoryTrimmer.trim`、`ClipboardHistoryPolicy.shouldCapture`），**不覆盖运行路径**。
- 全仓检索 `NSPasteboard(name:` → **零命中**（实测）；检索 `--ctl` / `CLIPSHELF_CONTROL_SOCKET` / `CLIPSHELF_PASTEBOARD_NAME` → **均零命中**（实测）。

## 3. Scope

**受影响**：`Sources/ClipShelfLite/Support/`（新增剪贴板接缝与控制服务）、`Stores/ClipStore.swift`（剪贴板来源改为可注入）、`Sources/ClipShelfLite/main.swift` 与 CLI 入口、`Support/SelfTest.swift`（新增断言）、`README.md`（若涉及用户可见行为）。

**不得改变的部分**
- **默认行为必须逐字节一致**：不设置任何环境变量时，实例仍监听 `NSPasteboard.general`、仍使用真实数据目录与标准 `UserDefaults`、**不开启任何监听端点**。
- `history.json` 的字段与格式；既有的设置入口、文案与布局。
- 既有 `--self-test <报告路径>` 的行为与报告格式（只允许**新增**断言，不得改动既有断言语义）。
- 不新增第三方依赖；不引入需要开发者账号的能力（签名证书 / 公证）；保持 ad-hoc 签名。

## 4. Constraints

1. **剪贴板接缝**：新增环境变量（建议名 `CLIPSHELF_PASTEBOARD_NAME`）。被设置时，该实例改监听 `NSPasteboard(name:)` 指定的**具名剪贴板**；未设置时用 `NSPasteboard.general`。
2. **控制端点**：新增环境变量（建议名 `CLIPSHELF_CONTROL_SOCKET`）指定一个**本地 Unix domain socket** 路径。**仅当该变量被设置时**实例才开启监听；未设置时**完全不创建、不监听**。
3. **客户端入口**：新增 CLI 模式（建议 `ClipShelf --ctl <子命令> [参数]`），作为客户端连接上述端点。结果以**机器可读形式**（建议 JSON）写到 stdout；以退出码表达成败（成功 `0`，失败非 `0`，连接不上必须是非 `0` 并给出可读错误）。
4. **必须具备这些能力**（子命令名可自定，但能力须齐，且须在报告中列出最终命令表）：
   - **探活**：返回进程存活与版本。
   - **读状态**：返回当前记录数、历史上限、是否记录中、数据目录路径。
   - **导出记录**：把**内存中**的记录导出为 JSON 到指定路径（用于断言真实运行路径的结果，而非只读磁盘文件）。
   - **注入剪贴板内容**：把文本（以及可选的具名 pasteboard 类型）写入**该实例正在监听的**那个剪贴板。
   - **改历史上限**：走**真实**的 `ClipStore.setHistoryLimit(_:)` 路径（含立即裁剪与落盘）。
   - **清空历史**：走真实的 `ClipStore.clearHistory()` 路径。
   - **优雅退出**：让实例正常收尾退出。
5. **安全**：socket 仅本用户可访问（权限 `0600`），路径必须落在隔离目录内；**不得监听 TCP 端口**。
6. **不得干扰轮询节奏**：控制服务不得阻塞主线程，也不得改变 `ClipStore` 既有 0.45s 轮询周期。
7. **测试断言一律加进 prompt-31 已建立的底座**（`ClipShelfSelfTest.run()` 唯一实现 + `--self-test` + `MacTests/ClipShelfLiteTests/SelfTestTests.m` 桥接），**不得引入 XCTest / Swift Testing，不得要求安装 Xcode**（见 `prompts/README.md`「环境约束」）。
8. **不得为了让结果通过而弱化断言。** 若确需调整既有行为，须在报告中说明理由。
9. 本项允许分两步交付（先剪贴板接缝、后控制通道），但**第 5 节的验收必须整体通过**才算完成。

## 5. Acceptance

- **A** 未设置 `CLIPSHELF_CONTROL_SOCKET` 时：实例**不开启**任何监听（可用 `lsof`/`nc` 验证无 socket）；此时 `--ctl` 连接失败，输出可读错误且退出码非 0。
- **B** 设置后，脚本能完成 探活 / 读状态 / 导出记录 / 改上限 / 清空 / 退出 六项，均返回机器可读结果且退出码正确。
- **C** 设 `CLIPSHELF_PASTEBOARD_NAME` 为某具名剪贴板后：向 `NSPasteboard.general` 写入内容，该实例历史**不增加**；向该具名剪贴板写入内容，历史**增加**。
- **D** **不干扰真实使用**（本项最关键）：完整跑一遍 A–C 之后，用户真实 `~/Library/Application Support/ClipShelf/history.json` 的 **SHA-256 不变**，且标准 `UserDefaults` 域中 `history.maxItems` / `clipboardHistory.enabled` **无变化**。
- **E** 经控制通道调用「改历史上限」为 5 后，实例**内存与磁盘**上的记录数**立即** ≤ 5，且置顶记录优先保留。
- **F** 经控制通道调用「清空历史」后，内存与磁盘记录数均为 0，且**不删除任何原文件**。
- **G** **默认路径回归**：不设任何环境变量启动，采集 / 去重 / 裁剪 / 持久化 / 敏感内容排除的行为与 prompt-32 交付时**一致**。
- **H** `swift build` 与 `swift test` 通过；`swift test` 至少覆盖：端点默认关闭、客户端连接失败的错误码、具名剪贴板隔离。
- **I** `ClipShelf --self-test <报告>` 退出码 0。
- **J** 报告须包含：改动文件清单、测试输出、**最终命令表**，以及一段**可直接复现的脚本示例**（启动隔离实例 → 注入 → 断言 → 退出）。
- **K** 报告须说明：`tests/parity/`（归 WorkBuddy 所有）**无需改动**即可在新接缝下运行；若确有必要改动，只**描述**需要什么，**不要动手改**。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **外部验收**：WorkBuddy 将在交付后用 `CLIPSHELF_PARITY_DELIVERED=31,32,43` 跑 `tests/parity/run.sh`，并**新增**一项「不退出用户实例即可运行」的用例。`tests/parity/` 归 WorkBuddy 所有，**Codex 不得修改**。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。（`git commit` / `push` 由用户本人执行。）
- **提交信息建议**：`Add runtime control channel and named pasteboard seam for scripted testing`
