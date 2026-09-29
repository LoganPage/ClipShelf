# prompt-45：`--self-test` 不再往 `~/Library/Preferences/` 泄漏 plist

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定、编号与两端口径对齐见 `prompts/README.md`。

> **来源**：**不是** Windows 交接清单里的项，而是**批次 C 独立验收时发现的 P3-2**（见 `prompts/batch-C-verification-report.md` §9）。属卫生问题，不影响功能，但每跑一次自检就往用户家目录扔一个文件。
> **依赖**：prompt-31 建立的测试底座（`ClipShelfSelfTest.run()`）。

## 1. Goal

1. **连续运行 `ClipShelf --self-test` 任意多次，`~/Library/Preferences/` 里 `ClipShelf.SelfTest.*.plist` 的数量不再增长。**
2. 退一步的目标（若 1 无法做到绝对）：**至多残留 1 个、且不随运行次数增加**。
3. 补一条断言把这件事钉住，否则以后又会退回去。

## 2. Evidence

**实测环境**：2026-09-29，仓库 `/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`，HEAD **`39067b3`**，Apple Swift 6.4 / Xcode 27.0。以下均为本次实测值。

### 2.1 泄漏点（已核实）

`Sources/ClipShelfLite/Support/SelfTest.swift:11-23`：

```swift
let temporaryRoot = fileManager.temporaryDirectory
    .appendingPathComponent("ClipShelf-SelfTest-\(UUID().uuidString)", isDirectory: true)
let suiteName = "ClipShelf.SelfTest.\(UUID().uuidString)"          // ← L14，每次一个全新 UUID
let environment = [
    "CLIPSHELF_DATA_DIR": temporaryRoot.path,
    "CLIPSHELF_DEFAULTS_SUITE": suiteName                          // ← L17
]

defer {
    try? fileManager.removeItem(at: temporaryRoot)                  // ← L21，这个**有效**
    UserDefaults.standard.removePersistentDomain(forName: suiteName) // ← L22，这个**无效**
}
```

- `suiteName` 被注入 `CLIPSHELF_DEFAULTS_SUITE`（另见 `SelfTest.swift:228`），`Support/AppEnvironment.swift:29-39` 据此返回 `UserDefaults(suiteName:)`。
- **`SelfTest.swift:126-135`** 的 `defaults suite isolation` 断言**会真的往该 suite 写一个值**（`defaults.set("isolated", forKey: defaultsKey)`，L128）→ 触发该域落盘。
- `SelfTest.swift:145` 又新建一个 `UserDefaults(suiteName: suiteName)`。

### 2.2 实测现象（已核实）

| 时点 | `ls ~/Library/Preferences \| grep -c '^ClipShelf\.SelfTest\.'` |
| --- | --- |
| 2026-09-29 13:58 | **65** |
| 2026-09-29 14:06 | **77** |
| 2026-09-29 14:35 | **86** |

约 11 次自检 → +12，**每次运行恰好 +1**（`swift test` 也 +1，因为与 `--self-test` 共用同一入口）。

- **临时数据目录确实被清干净了**（`temporaryRoot` 残留 = 0）→ 只有 **defaults 域**在泄漏。
- **根因不是路径写错**：`UserDefaults.standard.removePersistentDomain(forName:)` 调用本身合法，但 **cfprefsd 异步回写** —— 进程退出前那次删除没有落到磁盘，`~/Library/Preferences/ClipShelf.SelfTest.<UUID>.plist` 就留下了。每个文件 42 字节，内容是那个 `selfTest.<UUID>` 键。

### 2.3 同一根因的另一处（**不在本份范围**）

`~/Library/Preferences/ClipShelf.Runtime43.48183.plist` —— 来自 `tests/parity/runtime_control.sh` 的具名 suite。**该目录归 WorkBuddy 所有，Codex 不得修改**；用户侧另有一次性清理。**本份不要动它。**

## 3. Scope

**受影响**
- `Sources/ClipShelfLite/Support/SelfTest.swift`（suite 生命周期）
- 如确有必要，可一并改 `Sources/ClipShelfLite/Support/AppEnvironment.swift`

**不得改变的部分**
- **`CLIPSHELF_DEFAULTS_SUITE` 的语义不变**：有该环境变量时仍必须返回**隔离的** `UserDefaults(suiteName:)`，**不得为了省事退回 `.standard`**（那会让自检写进用户真实偏好，比泄漏严重得多）。
- **`--self-test` 的报告格式不变**：`tests/parity/run.sh` 的 T11 会解析该报告（断言名 + `success` 文案）。**报告路径、字段、退出码语义一个都不许改。**
- **`tests/parity/` 目录归 WorkBuddy 所有，不得修改。**
- **`~/Library/Preferences/` 下其它文件一个都不能动**：`ClipShelf.plist`、`local.codex.ClipShelf.plist`、`local.codex.ClipShelfLite.plist`、`ClipShelf.Runtime43.*.plist` —— **禁止任何"清空 Preferences 目录"式的实现**。本项只允许动**自己创建的那个 suite**。
- 既有 39 条断言**一条都不许改、不许删**（尤其 `defaults suite isolation` 与 `history limit persistence`）。

## 4. Constraints

1. **必须实测验证，不接受"理论上会清掉"。** 报告里给出**连续 3 次运行前后**的实测文件数。
2. **方向建议（不强制）**：`suiteName` 里那个 `UUID()` 是问题的一半 —— 每次都是新域名，谁也回收不了。把它换成**固定域名**（如 `ClipShelf.SelfTest`），配合**运行前 + 运行后各清一次**，最坏也只是残留 1 个而不是无限增长。是否采用由你判断，但**必须在报告里说明你选了哪条路、为什么**。
3. **新增断言**（加进 `ClipShelfSelfTest.run()`，与现有断言同风格）：
   - 名称建议：`self test defaults suite leaves no plist behind`
   - 判据：一次运行结束后，`~/Library/Preferences/` 里属于**本次 suite** 的 plist **不存在**。
   - ⚠️ **若该断言受 cfprefsd 时序影响而天然不稳定**，就退一步断言**「文件数不增长」**（例如比较运行前后匹配 `ClipShelf.SelfTest*` 的文件个数）。**两条路选一条即可，但必须在报告里写明选了哪条、以及你实测的稳定性（连续跑几次都过）。**
   - **绝不允许**为了让断言稳定而把它写成永真（如 `condition: true`）或删掉它。
4. **测试断言一律加进现有底座**（`ClipShelfSelfTest.run()` 唯一实现 + `--self-test <报告>` + `MacTests/ClipShelfLiteTests/SelfTestTests.m` 桥接）。**本份不迁移测试框架** —— 底座是否迁到 XCTest / Swift Testing 是独立的产品决策（见 `prompts/README.md`「环境变更（2026-09-29）」），**不要混进本份**。
5. **不得为了让结果通过而弱化断言。**
6. 构建须用 `swift build --disable-sandbox` / `swift test --disable-sandbox`。

## 5. Acceptance

- **A** 连续运行 `ClipShelf --self-test <报告>` **3 次**，每次跑完立刻统计：
  ```bash
  ls -1 ~/Library/Preferences/ | grep -c '^ClipShelf\.SelfTest\.'
  ```
  **3 次的数字不递增**（给出三个实测数字）。若走「固定域名」路线，理想结果是恒定 `0` 或恒定 `1`。
- **B** 新增断言后：`swift build --disable-sandbox` 通过；`swift test --disable-sandbox` 通过；`ClipShelf --self-test <报告>` **退出码 0**。断言数 **39 → 40**。
- **C** 回归：
  - `defaults suite isolation`、`history limit persistence` 两条既有断言仍通过；
  - `CLIPSHELF_DEFAULTS_SUITE` 仍产生隔离域（不得变成 `.standard`）—— 报告里给出证明；
  - `swift test --disable-sandbox` 退出码 **0**；
  - `~/Library/Preferences/` 下 `ClipShelf.plist` / `local.codex.*` / `ClipShelf.Runtime43.*` 的 **mtime 不变**（给出实测前后时间戳）。
- **D** 报告：`prompts/prompt-45-self-test-defaults-no-leak-report.md`，含改动文件清单、3 次运行的实测文件数、你选的路线与理由、新断言、以及**新断言稳定性实测**（连续跑 5 次都过）。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查。
- **提交信息建议**：`Stop leaking self-test defaults domains`
