# prompt-47：版本号收敛到单一来源，并在窗口标题显示运行版本

依据 `AGENTS.md` 的 Prompt and collaboration guidelines。协作约定、编号与两端口径对齐见 `prompts/README.md`；本批全局口径与同批分工见 **`prompts/batch-E.md`**（**必读**）。

> **来源**：`docs/parity/windows-1.4.1-vs-macos.md` §5.1 + §5.2。Windows 1.4.1（tag `windows-v1.4.1`）新增「窗口标题显示运行版本」，macOS 侧没有；同时 macOS 自己的版本号**做完 12 份功能 + 3 份清理后一次都没涨过**，且**同一版本字符串在源码里读了两处、兜底值还不一致**。
> **依赖**：无。本批第一份。

## 1. Goal

1. **版本号收敛到单一来源**：全仓只允许**一个地方**读 `CFBundleShortVersionString`，其余全部走它。消除现在两处重复读取 + 兜底值不一致的问题。
2. **窗口标题显示运行版本**（对齐 Windows 1.4.1 的**功能**）：打包后的应用窗口标题显示 `ClipShelf 1.4.1`；**未打包**的裸二进制回退为 `ClipShelf`（**不带版本号**）。

## 2. Evidence（2026-09-30 实测，HEAD `5b899fc`）

### 2.1 现状

| 位置 | 内容 |
| --- | --- |
| `Sources/ClipShelfLite/App/AppDelegate.swift:85` | `window.title = "ClipShelf"` —— **不含版本号** |
| `Sources/ClipShelfLite/Services/AppUpdateChecker.swift:80-84` | `private struct AppVersion { static var current: AppVersion { let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String; return AppVersion(version ?? "0") } }` —— **第 1 处读取，兜底 `"0"`** |
| `Sources/ClipShelfLite/Services/RuntimeControlServer.swift:221-224` | `private var appVersion: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.2.0" }` —— **第 2 处读取，兜底 `"1.2.0"`** |
| `script/build_app_bundle.sh:7` | `APP_VERSION="1.2.0"` —— **版本值卡在 1.2.0** |
| `script/build_app_bundle.sh:60-61` | `<key>CFBundleShortVersionString</key>` / `<string>$APP_VERSION</string>` |

**为什么这是真问题**：

- `AppVersion.current` 兜底 `"0"` → 未打包运行时 `AppUpdateChecker` 认为当前版本是 `0` → `latestVersion > currentVersion`（`AppUpdateChecker.swift:47`）几乎恒真 → **「更新」按钮会一直亮**。
- `RuntimeControlServer` 兜底 `"1.2.0"` → `--ctl status` 报出一个**早就过期的假版本号**。
- `AppVersion` 是 `AppUpdateChecker.swift` 里的 `private struct`，`RuntimeControlServer` 够不着 —— **这正是重复读取的成因**。

### 2.2 未打包时没有 Info.plist

`.build/debug/ClipShelf` 是 SwiftPM 直接产出的裸可执行文件，**没有 Info.plist** → `Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString")` 返回 **nil**。
→ 所以「标题显示版本」必须明确 nil 分支的表现，否则验收时无法判定。

### 2.3 Windows 1.4.1 的对应实现（只作功能参照，**不要照抄做法**）

`ClipShelf/MainWindow.xaml.cs`（提交 `1be0ec6`）：

```csharp
Title = WindowTitleText.Text = $"ClipShelf {WindowsUpdateService.CurrentVersion}";
```

Windows 是自绘标题栏，macOS 用 `NSWindow.title`，**表现方式本来就不同，只对齐「标题里能看到版本」这个功能**。

### 2.4 断言基线

`Sources/ClipShelfLite/Support/SelfTest.swift` 现有 **41 条**断言；最后一条是 `self test defaults suite file count does not grow`（**L671-680**）。
→ 本份的断言必须插在 `built-in text preview extension routing is narrow`（L659-669）之后、**L671 之前**（见 `batch-E.md` §3）。

## 3. Scope

**受影响**

- `Sources/ClipShelfLite/App/AppDelegate.swift` —— **只动 `showWindow()` 里的 `window.title` 一行**（L85 附近）
- `Sources/ClipShelfLite/Services/AppUpdateChecker.swift` —— 让 `AppVersion` 走单一来源
- `Sources/ClipShelfLite/Services/RuntimeControlServer.swift` —— 让 `appVersion` 走同一来源
- `script/build_app_bundle.sh` —— 只改 `APP_VERSION` 的值
- `Sources/ClipShelfLite/Support/SelfTest.swift` —— **只在 L671 之前追加**断言块

**建议新增**（放 `Support/`，与既有风格一致）

- `Sources/ClipShelfLite/Support/AppVersionInfo.swift` —— 单一来源 + **纯函数**，例如：
  ```swift
  enum AppVersionInfo {
      /// 唯一的 CFBundleShortVersionString 读取点
      static var bundleVersion: String?
      /// 未打包（nil）时必须回退为不带版本号的 "ClipShelf"
      static func windowTitle(bundleVersion: String?) -> String
      /// --ctl status 用的版本字符串
      static func statusVersion(bundleVersion: String?) -> String
  }
  ```

**不得改变的部分**

- **`--ctl status` 的 JSON 字段名与结构**（`tests/parity/` 与 `tests/parity/runtime_control.sh` 依赖它）。`version` 字段**仍须存在**。
- `AppUpdateChecker` 的比较语义：仍是 `latestVersion > currentVersion`；仍用 `/releases/latest`。
- `Package.swift`、`tests/parity/`、`docs/`、`README.md`、`Assets/` 一律不动。
- **其余 41 条断言一条都不许改、不许删。**
- 不改任何 UI 布局、间距、配色（本份**不碰美术风格**）。

## 4. Constraints

1. **单一来源是硬要求**：实测基线是 **2 处**（`Services/RuntimeControlServer.swift:222`、`Services/AppUpdateChecker.swift:82`），交付后 `grep -rn "CFBundleShortVersionString" Sources/` **必须只剩 1 处**（就在新的 `AppVersionInfo` 里）。
2. **未打包（nil）时标题必须是 `ClipShelf`**，**不得**出现 `ClipShelf 0`、`ClipShelf 1.2.0` 之类。
3. **显示逻辑必须是纯函数**（接收 `String?`、返回 `String`），以便断言覆盖两条分支 —— 不要写成直接读 `Bundle.main` 的不可测函数。
4. **版本值设为 `1.4.1`**。
   > ⚠️ **这是本提示词的假设**：与 Windows 1.4.1 对齐，让两端版本可比。若用户另有版本线，**只需改 `script/build_app_bundle.sh:7` 那一个常量**，其余不动。
5. `RuntimeControlServer` 的版本**不得**再返回硬编码的 `"1.2.0"`。未打包时返回什么由你定（建议 `"dev"` 或空串），但**必须在报告里写明**，并说明是否影响 `tests/parity/`。
6. **不引入第三方依赖。**
7. 不改产品行为（除标题与版本字符串外，运行时行为应完全一致）。

## 5. Acceptance

- **A** `swift build --disable-sandbox` 通过；`swift test --disable-sandbox --scratch-path /tmp/cs-batch-e` 通过（**不要**用仓库内的 `.build`，会因 iCloud 签名失败）。贴原始输出的关键段。
- **B（打包后）** 跑 `./script/build_app_bundle.sh`，启动 `dist/ClipShelf.app`，**窗口标题显示 `ClipShelf 1.4.1`**。
  - 用 `osascript -e 'tell application "System Events" to get name of front window of (first process whose name is "ClipShelf")'` 或截图作为证据，**贴原文**。
- **C（未打包）** 启动 `.build/debug/ClipShelf`，**窗口标题只显示 `ClipShelf`**（无版本号）。同样贴证据。
- **D** 新增断言（**至少 1 条，建议 2 条**），必须覆盖：
  - 打包值分支：`windowTitle(bundleVersion: "1.4.1") == "ClipShelf 1.4.1"`
  - nil 分支：`windowTitle(bundleVersion: nil) == "ClipShelf"`
  - 并断言 `statusVersion` 与 `windowTitle` **取到的是同一个来源**（例如两者对同一输入返回的版本部分一致）。
- **E** 回归：
  - 其余 41 条断言**零改动、零删除**；`Result: 42/42 checks passed.`（或你实际新增条数对应的数字），退出码 0，**连跑 3 次**。
  - `grep -rn "CFBundleShortVersionString" Sources/` 从 **2 处降到 1 处**，贴输出。
  - 贴 `tests/parity/runtime_control.sh` 的输出，证明 `--ctl status` 的 JSON 结构未变。
- **F** 报告：`prompts/prompt-47-version-single-source-and-window-title-report.md`，含：改动清单（文件 + 行数）、A–E 的原始输出、**单一来源的 grep 证据**、`1.4.1` 这个假设值的显式标注。

## 6. Handoff

- **实现**：Codex。**独立测试与复核**：WorkBuddy（只读，不改源码）。
- **授权（未勾选即未授权）**：`[ ]` 本地检查 · `[ ]` Release 构建 · `[ ]` 覆盖安装 · `[ ]` `git commit` · `[ ]` `push` · `[ ]` GitHub 发布。
  **本轮建议**：仅本地检查（**B 项需要构建 `.app` 包，属本地检查范围，不算 Release 构建**）。
- **提交信息建议**：`Show the running version in the window title`
