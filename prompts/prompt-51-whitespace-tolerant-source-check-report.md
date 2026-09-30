# prompt-51 实施报告：加固右键菜单源码检查

## 完成状态

已完成。右键菜单源码检查现在容忍空白和换行，只在真实 `.contextMenu` 配对花括号块内检查声明清单与 `role: .destructive`，并且仅允许从 `.app` 包运行时在源码缺失的情况下放行。产品行为和真实菜单均未修改，断言总数保持 **44**。

## 改动清单

```text
38  2  Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift
 4  3  Sources/ClipShelfLite/Support/SelfTest.swift
```

另新增本报告。`Sources/ClipShelfLite/Views/MainView.swift` 最终 `git diff` 为空；未修改 `Package.swift`、`tests/parity/`、`docs/`、`README.md`、`Assets/` 或 `script/`。

## A：构建与测试

### `swift build --disable-sandbox`

最终原始输出：

```text
Building for debugging...
[2/5] ClipShelf-product
[4/7] ClipShelf-product
[5/7] ClipShelf-product
[7/9] ClipShelf-product
Build complete! (3.90秒)
build exit=0
```

### `swift test --disable-sandbox --scratch-path /tmp/cs-p51.A1Rfk9`

使用 `mktemp` 创建全新 `/tmp` 目录，避免复用仓库内构建缓存。关键原始输出：

```text
Build complete! (16.83秒)
[PASS] record context menu exposes the declared actions in order: The installed native row menu derives its ordered actions and destructive delete role from one declaration
[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
Test Suite 'All tests' passed at 2026-09-30 23:59:32.616.
Executed 0 tests, with 0 failures (0 unexpected) in 0.000 (0.002) seconds
✔ Test run with 0 tests in 0 suites passed after 0.001 seconds.
```

## B：三条反向验证

### B0：正常基线

```text
exit=0
Result: 44/44 checks passed.
- [x] record context menu exposes the declared actions in order: The installed native row menu derives its ordered actions and destructive delete role from one declaration
```

### B1：空白与换行容忍

临时把真实菜单的单行写法：

```swift
ForEach(ClipRowMenu.orderedActions, id: \.self) { action in
```

改成行为等价的多行写法：

```swift
ForEach(
    ClipRowMenu.orderedActions,
    id: \.self
) { action in
```

构建与自检原始输出：

```text
Build complete! (3.78秒)
build exit=0
self-test exit=0
Result: 44/44 checks passed.
- [x] record context menu exposes the declared actions in order: The installed native row menu derives its ordered actions and destructive delete role from one declaration
```

结论：换行和空白变化不再造成假红。随后立即恢复原单行写法。

### B2：菜单删除角色检查

临时只移除菜单删除按钮上的 `, role: .destructive`，工具栏等其它破坏性按钮保持不动。构建与自检原始输出：

```text
Build complete! (3.77秒)
build exit=0
self-test exit=1
Result: 43/44 checks passed.
- [ ] record context menu exposes the declared actions in order: The record context menu is missing, detached from its declaration, lacks a destructive delete role, or has divergent titles
```

失败列表只有目标断言，证明判据限定在 `.contextMenu` 块内，没有被工具栏的 `role: .destructive` 假通过。随后恢复删除按钮原代码。

### B3：开发环境源码缺失严格失败

临时把：

```swift
.appendingPathComponent("Views/MainView.swift")
```

改为：

```swift
.appendingPathComponent("Views/NoSuchFile.swift")
```

重新构建并用仓库裸二进制执行，原始输出：

```text
Build complete! (3.78秒)
build exit=0
self-test exit=1
Result: 43/44 checks passed.
- [ ] record context menu exposes the declared actions in order: The record context menu is missing, detached from its declaration, lacks a destructive delete role, or has divergent titles
```

结论：开发环境源码不可读时不再静默放行。随后恢复 `Views/MainView.swift` 路径。

### B4：完整还原证据

最终检查：

```text
MainView restored exactly: yes
```

`git diff -- Sources/ClipShelfLite/Views/MainView.swift` 无输出。`SelfTest.swift` 只剩以下本项改动：

```diff
+        let runningFromAppBundle = Bundle.main.bundlePath.hasSuffix(".app")
...
-                && (mainViewSource.map(ClipRowMenu.isDeclaredMenuInstalled(in:)) ?? true),
+                && (mainViewSource.map(ClipRowMenu.isDeclaredMenuInstalled(in:)) ?? runningFromAppBundle),
```

以及目标断言的成功、失败说明文案更新。其余 43 条断言没有改动。

## C：最终回归

恢复全部临时改动并重建后连续执行三次：

```text
repeat-1 exit=0
Result: 44/44 checks passed.
repeat-2 exit=0
Result: 44/44 checks passed.
repeat-3 exit=0
Result: 44/44 checks passed.
```

断言计数：

```text
44
```

断言名称仍为 `record context menu exposes the declared actions in order`，保留了对齐测试依赖的 `context menu` 子串。

## D：隔离对齐回归

执行：

```text
CLIPSHELF_PARITY_ISOLATED=1 CLIPSHELF_PARITY_DELIVERED=31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49 tests/parity/run.sh
```

最终原始关键输出：

```text
✔ PASS T11 --self-test 退出码 0，报告已生成
✔ PASS T11 --self-test 报告覆盖已交付提示词（31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49）的全部主题
Result: 44/44 checks passed.
✔ PASS T16 上限 10000 时进程未崩溃
✔ PASS T16 上限 10000 时 8 条全部累积
── 汇总 ──
通过 29   失败 0   跳过 1
```

唯一跳过项是脚本默认关闭的 T12 打包冒烟。隔离校验确认用户真实 `history.json` 未被修改。

## fail-open 判定依据

判据为：

```swift
let runningFromAppBundle = Bundle.main.bundlePath.hasSuffix(".app")
```

三种运行方式的行为如下：

| 运行方式 | `Bundle.main.bundlePath` 形态 | 源码缺失时 |
| --- | --- | --- |
| 仓库裸二进制 | 仓库 `.build/.../debug`，不以 `.app` 结尾 | 严格失败 |
| `swift test` | SwiftPM 测试运行包/`.xctest`，不以 `.app` 结尾 | 严格失败 |
| 分发 App | `.../ClipShelf.app`，以 `.app` 结尾 | 允许放行 |

`tests/parity/run.sh` 使用仓库构建出的裸二进制，并且仓库源码存在，因此仍执行严格源码检查；T11 已实测通过。这个调整不会削弱 parity，反而会在其源码路径意外失效时让 T11 明确失败。分发 `.app` 不携带 Swift 源码，因此保留有边界的放行，避免已安装应用的 `--self-test` 永久失败。

## 实现说明

- 使用正则 `#"\.contextMenu\s*\{"#` 容忍菜单修饰器空白。
- 从 `.contextMenu` 的首个 `{` 开始做花括号深度计数，提取完整配对块。
- 只在该块内用 `#"ForEach\(\s*ClipRowMenu\.orderedActions"#` 检查声明清单引用。
- 只在该块内用 `#"\brole:\s*\.destructive"#` 检查破坏性角色。
- 找不到菜单或配对块不完整时返回 `false`。
- 没有引入第三方依赖，也未改变任何产品 UI 或菜单行为。
