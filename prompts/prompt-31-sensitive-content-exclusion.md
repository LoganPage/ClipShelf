# prompt-31：缺口 1 · 剪贴板敏感内容排除（并随本项建立测试底座）

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定、编号来源与两端口径对齐见 `prompts/README.md`。

本提示词只交付**一个**可独立验证的目标：**被标记为敏感 / 临时的剪贴板内容不进入历史**。§4.6 的测试底座是该目标可被自动化验证的**必要条件**，随本项一起成形，不单独成篇（对应交接说明第七节「并行」）。

## 1. Goal

1. **主目标**：macOS 端采集剪贴板时识别并跳过被标记为「敏感 / 临时」的内容（密码管理器写入、被显式标记为隐藏的内容），使其**不进入** `history.json`。
2. **附带（必要）**：建立最小测试底座 —— SwiftPM test target（`swift test` 可跑）、`--self-test` CLI 报告入口、两条环境变量接缝。prompt-32 ~ 42 直接复用，不再重复建设。

功能基准：Windows 端 ClipShelf 1.4.0，`Services/NativeClipboard.cs` 检查 `CanIncludeInClipboardHistory` 格式后决定是否入库。

## 2. Evidence

**实测环境**：2026-09-27，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`，HEAD `355acec`（内容与 Windows 侧参考提交 `0ab74e8` 逐字节等价，已逐 blob 校验）；Apple Swift 6.2.3，`swift build` 实测可跑。

- `Sources/ClipShelfLite/Stores/ClipStore.swift:115-134` `pollPasteboard()`：只有两道 `guard`（`isClipboardHistoryEnabled`、`changeCount` 变化），随后依次尝试 file / text / image，**没有任何类型排除检查**。
- 全仓检索 `concealed`、`transient`、`org.nspasteboard`、`CanIncludeInClipboardHistory` → **零命中**（实测）。
- `Stores/ClipStore.swift:19` `private let maxItems = 100`；`Package.swift:12-17` 只有 `.executableTarget`，**无 test target**；`App/ClipShelfLiteApp.swift:3` 用 `@main`，全仓无 `main.swift`。
- 截图监听是**独立路径**（`Services/ScreenshotFolderWatcher.swift` → `ClipStore.addScreenshot(data:sourceURL:)`，L63-74），不经过 `pollPasteboard()`。
- 已有外部黑盒验收脚本 `tests/parity/`（WorkBuddy 所有，**不属你的改动范围**），它依赖 §4.6 的三条接缝。

## 3. Scope

**受影响**：`Stores/ClipStore.swift`；`Package.swift`（新增 test target）；新增测试文件；如需新增判定类型则放 `Support/`；`README.md` 功能列表（中英双语）。

**不得改变的部分**
- 无标记内容的采集行为：文字 / 文件 / 图片的既有优先级与去重规则（`ClipStore.swift:297-351`）保持不变。
- `history.json` 的字段与格式（`Models/ClipItem.swift`）。
- `script/*.sh`、`Assets/`、`Package.swift` 中既有的产品名与可执行目标名。
- Windows 端仓库；`tests/parity/` 目录。

## 4. Constraints

1. **判定必须在写入历史之前**，且**不得**影响 `changeCount` 记账：被排除的内容同样要推进 `changeCount`，否则轮询会反复处理同一次复制。
2. 只依赖 AppKit / Foundation，**零第三方依赖**。
3. 判定不确定（读不到类型信息）时**按不敏感处理**（保守入库），不得因异常丢内容。
4. macOS 侧通行约定（具体实现自定）：`org.nspasteboard.ConcealedType`、`org.nspasteboard.TransientType`。
5. **数据隔离接缝**（供 `tests/parity/` 使用，接口须稳定）：
   - `CLIPSHELF_DATA_DIR`：设置时用该目录存放 `history.json`；未设置时保持 `~/Library/Application Support/ClipShelf`。
   - `CLIPSHELF_DEFAULTS_SUITE`：设置时改用 `UserDefaults(suiteName:)`；未设置时保持现有默认域。
6. **测试底座**：
   - 新增 SwiftPM test target，`swift test` 必须可跑通过。
   - `ClipShelf --self-test <报告路径>`：在隔离临时目录内跑同一套断言，写报告文件，**退出码 0 = 全通过**；失败项打印可读原因；跑完自动退出，不常驻、不弹窗、不读写真实剪贴板、不要求窗口服务器交互。
   - 断言逻辑**只实现一次**：`swift test` 与 `--self-test` 共用同一份实现，不要各写一套。
   - 无参数启动的行为必须与现在**完全一致**。
7. **不得为了让结果通过而弱化断言**。若确需调整既有行为，须在报告中说明理由。

## 5. Acceptance

- **A** 复制带 `org.nspasteboard.ConcealedType` 标记的文本 → 等 2 秒 → `history.json` **不新增**记录。
- **B** 带 `org.nspasteboard.TransientType` 标记的内容同样不入库。
- **C** 不带任何标记的文本 / 文件 / 图片照常入库，条数与内容与改动前一致。
- **D** 排除动作不会造成同一次复制被反复处理（`changeCount` 记账正确）。
- **E** `swift build` 通过；`swift test` 通过，且至少覆盖「带标记不入库」「不带标记入库」两个用例。
- **F** `ClipShelf --self-test <报告>` 退出码 0，报告文件生成。
- **G** 设 `CLIPSHELF_DATA_DIR=<临时目录>` 启动时，读写的是该目录下的 `history.json`；不设时行为不变。
- **H** 设 `CLIPSHELF_DEFAULTS_SUITE=<临时域>` 时设置读写走该域。
- **I** `README.md` 中英双语功能列表补充本项（隐私相关）。
- **J** 报告：改动文件清单、`swift test` 与 `--self-test` 的实测输出、手工验证步骤。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查（`swift build` + `swift test` + `--self-test`）。
- **提交信息建议**：`Skip concealed/transient clipboard content and add test harness`
- **已定**：`--self-test` 与 SwiftPM test target **双入口长期并存**（见 `prompts/README.md` §3.6 与 §7）。断言逻辑只实现一次。
