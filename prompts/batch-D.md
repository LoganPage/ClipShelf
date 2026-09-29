# 批次 D（收尾 2 份）：44 → 45

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定见 `prompts/README.md`。

---

## 0. 本文件的性质（先读这段）

本文件**不是新的功能规格**，而是一份**批次执行指令**。

功能规格是下列 **2 份提示词文件**，必须逐份**完整读取**并执行：

| 顺序 | 文件 | 主题 |
| -- | --- | --- |
| 1 | `prompts/prompt-44-generic-file-type-neutral-color.md` | 通用文件图标改中性灰 + 补颜色断言 |
| 2 | `prompts/prompt-45-self-test-defaults-no-leak.md` | `--self-test` 不再泄漏 `ClipShelf.SelfTest.*.plist` |

**执行顺序：44 → 45。** 顺序理由见 §1.1。

> **这两份不是 Windows 交接清单里的项**，而是**批次 C 独立验收时发现的两个 P3 问题**：
> - **P3-1** → prompt-44（prompt-37 的「表格」与「通用回退」配色视觉近似）
> - **P3-2** → prompt-45（自检往 `~/Library/Preferences/` 泄漏 plist）
>
> **已交付并独立验收通过的 12 份**为：31、32、43、34、35、33、38、37、40、41、36、42。**不要重做。**
> 39（缓存清理）**已拍板不做**。本批是**功能对齐之外的收尾清理**，做完即告一段落。

---

## 1. 为什么合并成一批

用户 2026-09-28 要求提速：「一次让 codex 做多几个功能，一个一个来太慢了」。这两份都很小（合计预计 < 80 行改动），分两次交付纯属浪费往返。

批次**只减少往返次数**，**不改变**任何一份提示词的目标、约束或验收标准。

### 1.1 顺序依据

- **44 先做**：它只动 `Support/AppTheme.swift` + `SelfTest.swift` 的**颜色断言区**，边界最清楚，风险最低。
- **45 后做**：它要动 `SelfTest.swift` **开头（L11-23）的 suite 生命周期**，是 `run()` 里最"地基"的一段。先让 44 把自己的断言加完，45 再动地基，避免两份同时改 `run()` 的头部区域。
- 两份**没有功能依赖**，只是**降低同批互撞的概率**。

### 1.2 分工（**硬性，避免同批互相打架**）

**两份都会改 `Sources/ClipShelfLite/Support/SelfTest.swift`**，必须分区：

| 提示词 | 允许改 `SelfTest.swift` 的哪部分 | 禁止 |
| --- | --- | --- |
| **44** | **只在「文件类型图标断言区」追加**一条新断言（现有 `file type icons remain visually distinct`，约 **L527-531** 附近） | 不得改 `run()` 的 L11-23、不得改 L126-151（defaults 相关）、不得改任何既有断言 |
| **45** | **只改 L11-23**（suite 生命周期）与**追加**自己那条新断言（放在 `run()` 末尾断言区） | 不得改颜色相关断言、不得改 L519-531 |

**共同的硬性规则**：

1. **只追加，不修改。** 既有 **39 条断言一条都不许改、不许删**（`defaults suite isolation`、`history limit persistence`、`file type icons remain visually distinct` 都在保护范围内）。
2. **两份都不得动 `Views/MainView.swift:1147-1150` 的调用方式。**
3. **两份都不得动 `tests/parity/`**（该目录归 WorkBuddy 所有）。
4. **两份都不得改 `--self-test` 的报告格式**（`tests/parity/run.sh` 的 T11 依赖它）。
5. **两份都不迁移测试框架** —— 底座是否迁到 XCTest / Swift Testing 是独立决策（见 `prompts/README.md`「环境变更（2026-09-29）」），**不要混进本批**。

---

## 2. 环境（2026-09-29 起已变，务必按新环境做）

- `xcode-select -p` = `/Applications/Xcode.app/Contents/Developer`，工具链是 **Apple Swift 6.4 / Xcode 27.0**。
- **不再需要** `DEVELOPER_DIR=/Library/Developer/CommandLineTools` 绕过。
- 构建/测试命令：
  ```bash
  swift build --disable-sandbox
  swift test --disable-sandbox
  .build/debug/ClipShelf --self-test /tmp/selftest.md
  ```
- 现状基线：`swift test` **0 FAIL**、退出码 **0**；`--self-test` **39/39、退出码 0**。（`swift test` 的 `[PASS]` 行会因 C 桥接与 Swift-Testing 各跑一遍而**翻倍**，属正常，不要当成新增断言。）

---

## 3. 每份的节奏（不得省略）

每份做完**立刻**：

1. `swift build --disable-sandbox`
2. `swift test --disable-sandbox`
3. `.build/debug/ClipShelf --self-test /tmp/<编号>-selftest.md`
4. 写 `prompts/prompt-<编号>-<主题>-report.md`

**某一份卡住时**：**不要**为了整批通过而弱化断言 —— 停下该份、写清原因、**继续后面那份**。

---

## 4. 每份必须新增的断言（硬性）

| 提示词 | 必须新增 | 断言数 |
| --- | --- | --- |
| 44 | `file type icon colors are pairwise distinct`（遍历 7 个 category，任意两类（背景,前景）不同；`.generic` 与 `.spreadsheet` 色距 ≥ 0.08） | 39 → **40** |
| 45 | `self test defaults suite leaves no plist behind`（或「文件数不增长」等价形式） | 40 → **41** |

> 现有 `file type icons remain visually distinct`（`SelfTest.swift:527-531`）**只比 SF Symbol 名，不比颜色** —— 这正是 prompt-44 要补的缺口，**不要**以为它已经覆盖了。

---

## 5. 报告要求

- 每份一份：`prompts/prompt-44-generic-file-type-neutral-color-report.md`、`prompts/prompt-45-self-test-defaults-no-leak-report.md`。
- 每份报告必须含：**改动文件清单**（含行数增删）、**测试输出**、**验收项逐条对照（A/B/C/D/E）**、**手工验证步骤**。
- prompt-44 必须附：**实际采用的 RGB 值**与**实算色距**；`AppTheme.swift` 的 diff（证明只有 `.generic` 相关行变化）。
- prompt-45 必须附：**连续 3 次运行前后的实测文件数**、**你选的路线与理由**、**新断言的稳定性实测（连续 5 次都过）**。

**另外**：本批结束请额外产出一份**批次小结** `prompts/batch-D-report.md`，含：

- 两份的完成状态；
- **断言总数**（应为 **41**）；
- 一句「本批未改动 `Views/MainView.swift` / `Stores/` / `Services/` 的其它文件」的确认（若确实改了，逐条列出并说明）；
- 遗留问题。

> 上一批（B2）曾漏交批次小结，批次 C 已补。**本批不要漏。**

---

## 6. 红线（违反即本批不通过）

1. **不得改、不得删任何既有断言**（39 条全在保护范围）。
2. **不得让 `--self-test` 写进用户真实偏好**（`CLIPSHELF_DEFAULTS_SUITE` 必须继续产生隔离域）。
3. **不得动 `~/Library/Preferences/` 下的其它文件**（`ClipShelf.plist` / `local.codex.*` / `ClipShelf.Runtime43.*`）。
4. **不得动 `Views/MainView.swift:1147-1150` 的调用方式。**
5. **不得修改 `tests/parity/`。**
6. **不得引入第三方依赖**；只用 Apple 随系统提供的框架。
7. **不得把断言写成永真**（如 `condition: true`）来凑通过。

---

## 7. 交付后

- 由 WorkBuddy 独立复核（代码审查 + 断言完整性 + 变异测试 + 黑盒回归 + 用户数据未被触碰）。
- **`git commit` / `push` / 发布一律由用户本人执行**；Codex 与 WorkBuddy 都不做，也不必请示。
