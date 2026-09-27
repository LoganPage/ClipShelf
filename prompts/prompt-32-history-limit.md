# prompt-32：缺口 2 · 历史上限可调（1–10000）

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。
前置：**prompt-31 已于 2026-09-27 验收闭环**（18 通过 / 0 失败 / 4 有意跳过；测试底座 `ClipShelfSelfTest.run()` + `--self-test` 可用）。本提示词只交付**历史上限可调**这一件事。

## 1. Goal

把写死的历史上限改为用户可调：设置面板中可设 1–10000，默认 100；**调低后立即裁剪最旧的未置顶记录并落盘**；重启后保持。

功能基准：Windows 端 ClipShelf 1.4.0，`SettingsPanel.cs` 的 `HistoryLimitInput` / 设置字段 `MaxItems`，范围 1–10000，调低后立即裁剪。

## 2. Evidence

**实测环境**：2026-09-27 复测，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`，HEAD `355acec`；行号按**已含 prompt-31 改动的工作区**实测（prompt-31 尚未提交，故 HEAD 未变）。

- `Sources/ClipShelfLite/Stores/ClipStore.swift:19`：`private let maxItems = 100` —— **硬编码常量**。
- `Stores/ClipStore.swift:304-306`：`add(_:)` 中 `if items.count > maxItems { trimToMaxItems() }`。
- `Stores/ClipStore.swift:321-329`：`trimToMaxItems()` 已有「优先淘汰最旧未置顶」逻辑（`lastIndex(where: { !$0.isPinned })`），无置顶项时才 `removeLast()`。
- `Views/SettingsView.swift:157-269`：`historySection`（标题「历史」）已含开关（L159）、点击行为设置、颜色设置、`查看历史存储位置`（L257）、`清空历史`（L261）与一句说明文案（L265）。**没有历史上限输入入口**。
- 全仓检索 `history.maxItems` → **零命中**（实测）。
- 偏好持久化既有范式可参考：`Support/AppearancePreferences.swift:28-41`（`AppEnvironment.userDefaults` + 变更通知）。
- 设置持久化接缝：prompt-31 已将全部设置读写从 `UserDefaults.standard` 改为 `AppEnvironment.userDefaults`（受 `CLIPSHELF_DEFAULTS_SUITE` 环境变量控制），新键必须沿用该接缝，否则隔离测试无法生效。

## 3. Scope

**受影响**：`Stores/ClipStore.swift`、`Views/SettingsView.swift`、新增 `Support/` 下的偏好类型（如需）、`README.md` 功能列表（中英双语）。

**不得改变的部分**
- 裁剪规则本身：**置顶项优先保留**，无置顶项时才淘汰最旧。
- 「不删除任何原文件」：裁剪只作用于 ClipShelf 记录与 `history.json`，绝不删除截图文件夹或访达里的文件。
- `history.json` 的字段与格式；既有的「清空历史」「查看历史存储位置」入口与文案。
- 设置面板既有布局风格（圆角 18、`settingsSection(_:)` 容器、caption 说明文字）。

## 4. Constraints

1. 上限持久化到 `AppEnvironment.userDefaults`（**不要**用 `UserDefaults.standard`），键名 `history.maxItems`，**默认 100**，合法范围 **1–10000**。外部验收脚本以 `defaults write <suite> history.maxItems -int 5` 预置该键，故须按**整数**读写（键缺失时取 100，不能把「缺失」与「0」混为一谈）。
2. setter 被调用后**立即**执行 `trimToMaxItems()` + `save()`，不需要重启。
3. **非法输入必须被规整而非崩溃**：空、非数字、0、负数、超过 10000 —— 一律回退到最近的合法值（或恢复上一个合法值），并在 UI 上体现最终生效值。
4. **未设置该键时的行为必须与现在完全一致**（等价于 100），旧用户升级后无感知。
5. 沿用 prompt-31 建立的测试底座：新增断言同时进 `swift test` 与 `--self-test`；断言逻辑只实现一次。
6. 不得为了让测试通过而弱化断言。

## 5. Acceptance

- **A** 把上限设为 5 → 历史立即只剩 5 条，且**置顶项优先保留**，被淘汰的是最旧的未置顶记录。
- **B** 重启后上限仍为 5。
- **C** 设为 10000 不崩溃；随后继续复制内容可正常累积。
- **D** 边界值 1 与 10000 均被接受；0、-1、10001、空串被规整到合法值。
- **E** 未设置该项时行为与 1.2.0 一致（等价 100 条）。
- **F** 裁剪不删除任何原文件（截图文件夹与访达文件不受影响）。
- **G** `swift build` 与 `swift test` 通过；`swift test` 至少覆盖：上限边界、调低后立即裁剪、置顶优先保留。
- **H** `ClipShelf --self-test <报告>` 退出码 0。
- **I** `README.md` 中英双语功能列表补充本项。
- **J** 报告：改动文件清单、测试输出、手工验证步骤（含非法输入的实测表现）。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **外部验收**：WorkBuddy 的黑盒套件 `tests/parity/run.sh` 已预置 **T07（历史上限可调）**，当前因本项未交付而 SKIP；本项交付后由 WorkBuddy 以 `CLIPSHELF_PARITY_DELIVERED=31,32` 启用实跑。`tests/parity/` 归 WorkBuddy 所有，**Codex 不得修改**。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Make clipboard history limit configurable (1-10000)`
