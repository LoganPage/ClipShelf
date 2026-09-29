# prompt-44：让「通用文件」图标与「表格」图标在颜色上真正可区分

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定、编号与两端口径对齐见 `prompts/README.md`。

> **来源**：**不是** Windows 交接清单里的项，而是**批次 C 独立验收时发现的 P3-1**（见 `prompts/batch-B2-verification-report.md` §6 与 `prompts/batch-C-verification-report.md` §9）。prompt-37 交付后，「表格」与「通用回退」两类图标在视觉上几乎一样，用户无法区分。
> **依赖**：prompt-31 建立的测试底座（`ClipShelfSelfTest.run()`）。

## 1. Goal

1. **`.generic`（通用回退）文件类型图标改用中性灰**，与其余 6 类（PDF 红 / 表格绿 / Word 蓝 / PPT 橙 / 压缩包紫 / 文件夹琥珀）在**颜色上**一眼可分，尤其**不得再与 `.spreadsheet` 撞色**。
2. **补一条颜色维度的断言** —— 现有「图标可区分」断言只比了 SF Symbol 名，颜色撞车目前**零覆盖**。

## 2. Evidence

**实测环境**：2026-09-29，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`，HEAD **`39067b3`**。以下均为本次实测值。

### 2.1 撞色的两对常量（已核实）

`Sources/ClipShelfLite/Support/AppTheme.swift`：

| 常量 | 行号 | 浅色 | 深色 |
| --- | --- | --- | --- |
| `filePreviewBackground`（被 `.generic` 用） | **53-56** | `(0.91, 0.96, 0.92)` | `(0.13, 0.24, 0.18)` |
| `spreadsheetPreviewBackground`（`.spreadsheet`） | **69-72** | `(0.89, 0.97, 0.91)` | `(0.12, 0.25, 0.17)` |
| `filePreviewForeground`（被 `.generic` 用） | **57-60** | `(0.12, 0.38, 0.20)` | `(0.45, 0.86, 0.67)` |
| `spreadsheetPreviewForeground`（`.spreadsheet`） | **73-76** | `(0.08, 0.43, 0.20)` | `(0.43, 0.87, 0.59)` |

**浅色下背景色差 ΔRGB = (0.02, 0.01, 0.01)，前景色差 ΔRGB = (0.04, 0.05, 0.00)** —— 四舍五入到 8 位色就是同一个绿。这就是 P3-1 的成因：`.generic` 直接**复用了「文件」那一套绿色**，而那一套本来就是照 `.spreadsheet` 的绿做的。

### 2.2 映射处（已核实）

- `AppTheme.swift:134-144` `fileTypeBackground(_ category:)`，其中 **L142** `case .generic: filePreviewBackground`
- `AppTheme.swift:146-156` `fileTypeForeground(_ category:)`，其中 **L154** `case .generic: filePreviewForeground`

### 2.3 调用点（已核实，只有一处）

全仓检索 `filePreviewBackground` / `filePreviewForeground` → **仅** `AppTheme.swift:53`/`:57`（定义）与 `:142`/`:154`（使用），**无其它调用点**。

唯一消费方：`Sources/ClipShelfLite/Views/MainView.swift:1147` `.fill(AppTheme.fileTypeBackground(category))`、`:1150` `.foregroundStyle(AppTheme.fileTypeForeground(category))`。

→ **改这两个常量不会影响文件预览界面**（那是 `imagePreview*` / `textPreview*` 等另一组常量）。

### 2.4 覆盖缺口（已核实）

- `Support/SelfTest.swift:527-531` 的 `file type icons remain visually distinct`：条件是 `Set(FileTypeIconCategory.allCases.map(\.symbolName)).count == FileTypeIconCategory.allCases.count` —— **只比 SF Symbol 名，完全不比颜色**。
- `Support/SelfTest.swift:519-525` 的 `file type icon maps supported extensions and fallbacks`：只断言 category 映射，不断言颜色。

→ 所以本项的配色**一条断言都没有**，改错了也不会被测试发现。**这是本份必须补断言的理由。**

## 3. Scope

**受影响**
- `Sources/ClipShelfLite/Support/AppTheme.swift` —— `.generic` 用到的配色（改值或新增一对中性灰常量后改映射，二选一）
- `Sources/ClipShelfLite/Support/SelfTest.swift` —— **只追加**一条颜色断言

**不得改变的部分**
- **其余 6 个 category 的颜色值必须逐位不变**（PDF / 表格 / Word / PPT / 压缩包 / 文件夹）。
- `FileTypeIconCategory` 的成员、`CaseIterable` 顺序、`symbolName` 一个都不许动（prompt-37 的 `file type icons remain visually distinct` 依赖它）。
- `Views/MainView.swift:1147-1150` 的调用方式不变。
- 非文件类型配色（`imagePreview*` / `textPreview*` / `imageThumbnailBackground` / `actionButton*` / `overlayBackground` / `selectedActionBackground`）一个都不许动。
- 既有 39 条断言**一条都不许改、不许删**。

## 4. Constraints

1. **「中性灰」的定义**：R / G / B 三通道**最大差值 ≤ 0.02**（即肉眼读作灰，不带任何色相）。深浅两套都要给。**不要用纯 `(0.5,0.5,0.5)` 这种死灰** —— 要与现有界面协调，可参考同文件 `imageThumbnailBackground`（`AppTheme.swift:117-120`）的灰阶量级。
2. **可区分性**：`.generic` 与 `.spreadsheet` 在**浅色与深色**下的背景色距都必须 **≥ 0.08**（按 `max(|ΔR|,|ΔG|,|ΔB|)` 计），且与其余 5 类也不撞色。
3. **断言必须可比较**：`AppTheme` 返回的是 SwiftUI `Color`（内部是动态 `NSColor`，`AppTheme.swift:162-166`）。若无法直接比较，**请在 `AppTheme` 上补一个纯函数接缝**（例如按 category 返回 RGB 分量），由它**同时驱动**现有 `fileTypeBackground` / `fileTypeForeground`，**不要把颜色表复制一份到测试里**（复制会让断言失去鉴别力）。
4. **新增断言**（加进 `ClipShelfSelfTest.run()`，与现有断言同风格）：
   - 名称建议：`file type icon colors are pairwise distinct`
   - 条件：遍历 `FileTypeIconCategory.allCases`，任意两个 category 的（背景, 前景）组合都不相同；且 `.generic` 与 `.spreadsheet` 的色距 ≥ 0.08。
   - `success` / `failure` 文案要能指出是哪一类撞了。
5. **测试断言一律加进现有底座**（`ClipShelfSelfTest.run()` 唯一实现 + `--self-test <报告>` + `MacTests/ClipShelfLiteTests/SelfTestTests.m` 桥接）。**本份不迁移测试框架** —— 底座是否迁到 XCTest / Swift Testing 是独立的产品决策（见 `prompts/README.md`「环境变更（2026-09-29）」），**不要混进本份**。
6. **不得为了让结果通过而弱化断言。** 若你判断某项既有断言与本份冲突，**停下并说明**，不要自行改它。
7. 构建须用 `swift build --disable-sandbox` / `swift test --disable-sandbox`（外层沙箱会挡住 SwiftPM 自带的 `sandbox-exec`）。

## 5. Acceptance

- **A** `.generic` 的背景与前景在**浅色和深色**下都是中性灰（三通道最大差 ≤ 0.02）。报告里给出**你实际采用的两组 RGB 值**。
- **B** `.generic` 与 `.spreadsheet` 的背景色距在浅色与深色下都 ≥ 0.08（给出实算数字）；其余 5 类两两也不撞色。
- **C** 新增断言后：`swift build --disable-sandbox` 通过；`swift test --disable-sandbox` 通过；`ClipShelf --self-test <报告>` **退出码 0**。断言数 **39 → 40**。
- **D** 回归：**其余 6 个 category 的颜色值逐位不变** —— 报告里附 `AppTheme.swift` 的 diff，证明只有 `.generic` 相关行变化；`file type icons remain visually distinct` 与 `file type icon maps supported extensions and fallbacks` 两条既有断言仍通过。
- **E** 报告：`prompts/prompt-44-generic-file-type-neutral-color-report.md`，含改动文件清单、新配色数值与色距实算、`swift test` / `--self-test` 输出、手工验证步骤（打开浅色/深色，同时放一个 `.xlsx` 与一个无扩展名文件，确认两个图标可区分）。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Use a neutral gray for the generic file type icon`
