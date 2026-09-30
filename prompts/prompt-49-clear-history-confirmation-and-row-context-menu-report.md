# prompt-49 实施报告：清空确认与记录右键菜单

## 完成状态

已完成产品实现与自动回归。三个 UI 清空入口均在 UI 层确认，`ClipStore.clearHistory()` 与 `--ctl clear` 未改；记录行使用 SwiftUI 原生 `.contextMenu`，没有新增键位、自绘菜单或 Windows 视觉。

## 改动文件

```text
21  1  Sources/ClipShelfLite/App/AppDelegate.swift（本份部分）
30  0  Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift
20  0  Sources/ClipShelfLite/Support/SelfTest.swift（本份部分）
34  2  Sources/ClipShelfLite/Views/MainView.swift（本份部分）
15  1  Sources/ClipShelfLite/Views/SettingsView.swift
```

`Stores/ClipStore.swift` 与 `Services/RuntimeControlServer.swift` 的清空分支未被本份修改。

## A：构建与测试

### `swift build --disable-sandbox`

```text
Building for debugging...
[Planning deferred tasks]
[5/10] ClipShelf-product
[11/16] ClipShelf-product
[12/16] ClipShelf-product
[14/16] ClipShelf-product
Build complete! (4.78秒)
```

### `swift test --disable-sandbox --scratch-path /tmp/cs-batch-e`

```text
Build complete! (10.01秒)
[PASS] app version has one display source and safe development fallbacks: Window title and status version share the bundle version while unbundled builds stay identifiable
[PASS] clear history confirmation covers empty single and multiple histories: Only nonempty histories request confirmation and the copy reflects single or multiple records
[PASS] record context menu preserves existing action order and pin wording: The native row menu exposes copy, paste, pin, and delete in the expected order
[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
Test Suite 'All tests' passed at 2026-09-30 16:58:12.579.
Executed 0 tests, with 0 failures (0 unexpected) in 0.000 (0.002) seconds
◇ Test run started.
↳ Testing Library Version: 2084
↳ Target Platform: arm64e-apple-macos14.0
✔ Test run with 0 tests in 0 suites passed after 0.001 seconds.
```

## B：清空确认界面实测

测试使用独立临时数据目录和具名剪贴板，构造 `alpha`、`beta`、`gamma` 三条记录，没有读取或修改用户真实历史。

### 工具栏垃圾桶

清空选择后点击工具栏垃圾桶，实际界面树：

```text
0 sheet Description: alert
1 text 清空全部历史记录？ 将清空 3 条记录。清空后仍可使用 Command-Z 撤销。
2 button Description: 取消
3 button Description: 清空
```

点击「取消」后实际仍显示：

```text
gamma 文字
beta 文字
alpha 文字
```

### 设置页

设置页「清空历史」实际打开同一原生 sheet：

```text
0 sheet Description: alert
1 text 清空全部历史记录？ 将清空 3 条记录。清空后仍可使用 Command-Z 撤销。
2 button Description: 取消
3 button Description: 清空
```

点击「取消」后记录仍为 3 条。

### 空历史

用隔离控制通道清空临时历史后点击工具栏垃圾桶，窗口仍为普通主窗口，没有出现 sheet：

```text
Window: "ClipShelf 1.4.1", App: ClipShelf.
0 standard window ClipShelf 1.4.1
```

随后点击现有撤销按钮，`alpha`、`beta`、`gamma` 三条全部恢复，证明清空仍进入既有撤销栈。

### 人工覆盖边界

以下两个入口与破坏性确认按钮没有在本轮自动点击：

- 菜单栏状态项无法由当前只读界面驱动稳定定位；源码中的 `AppDelegate.clearHistory()` 已实际改为 `NSAlert`，可见窗口走 sheet，隐藏窗口走原生 modal alert。
- 本轮 UI 驱动未能向应用投递 Delete 键；Delete 与工具栏仍共用已实测的 `deleteSelectedOrClear()`。
- 为避免替用户执行界面中的破坏性操作，确认框的「清空」按钮未由 UI 自动点击。清空、保持撤销栈和恢复行为由既有 `clear history is undoable` 自检及上述隔离控制清空 + 撤销恢复实测覆盖。

因此本报告没有把这三项写成虚构的人工通过；建议人工复核时逐个点击一次菜单栏清空、Delete 和确认按钮。

## C：脚本控制通道回归

`tests/parity/runtime_control.sh` 原始汇总：

```text
── T-A 默认不开启端点 ──
✔ PASS T-A 未设 CLIPSHELF_CONTROL_SOCKET 时不创建控制 socket
✔ PASS T-A 未设 socket 时 --ctl 退出码 2 且输出可读 JSON
── T-B 控制通道基本能力 ──
✔ PASS T-B 隔离实例启动，控制 socket 已建立
✔ PASS T-B socket 权限为 0600
✔ PASS T-B ping 返回 ok
✔ PASS T-B status 初始记录数为 0
── T-C 与其它剪贴板隔离（关键） ──
✔ PASS T-C 向另一个具名剪贴板写入后，隔离实例仍为 0 条（只读自己的剪贴板）
── T-D 注入 / 导出 / 上限 / 清空 / 退出 ──
✔ PASS T-D 注入文本后实例记录数为 1
✔ PASS T-D export 导出内存记录成功（1 条）
✔ PASS T-D set-limit 5 生效
✔ PASS T-D clear 后记录数为 0
✔ PASS T-D quit 后进程正常退出
✔ PASS T-D 退出后 socket 已被移除
── T-E 真实数据保护 ──
✔ PASS T-E 用户真实 history.json 未被改动
✔ PASS T-E 历史上限写入了隔离偏好域
── 汇总 ──
通过 15   失败 0
```

这证明 `--ctl clear` 仍直接执行，不经过 UI 确认，也没有改变 JSON 结构。

## D：右键菜单实测

在隔离记录上右键，实际菜单树：

```text
复制
粘贴
置顶
删除
```

随后从 `gamma` 拖至 `alpha`，界面显示 `已选 3 条`，三行均保持选中背景，证明右键菜单关闭后拖选仍正常。

在三条选中记录中的 `beta` 上右键执行「复制」，具名剪贴板实测：

```text
items=1
["gamma\nbeta\nalpha"]
```

证明已选行的复制继续复用既有批量作用域。代码检查确认：未选行仍通过 `actionItems(for:)` 回落为单行；置顶和删除菜单直接调用单行 API，与行内按钮完全一致。

## E：回归

连续三次自检：

```text
self-test-1 exit=0
Result: 44/44 checks passed.
self-test-2 exit=0
Result: 44/44 checks passed.
self-test-3 exit=0
Result: 44/44 checks passed.
```

隔离对齐测试：

```text
CLIPSHELF_PARITY_ISOLATED=1 ... tests/parity/run.sh
通过 29   失败 0   跳过 1
T12 打包冒烟按脚本默认设置跳过；本批已单独成功运行 build_app_bundle.sh。
```

用户真实 `history.json` 在 T13、T15、T16 与总收尾检查中均报告未被改动。

## 只同步功能自查

- Windows 菜单样式：没有复制。
- Windows 键位：没有添加 `Shift+F10` 或其它新键位。
- Windows 对话框样式：没有复制；主界面与设置使用 SwiftUI 原生确认框，菜单栏使用 `NSAlert`。
- Windows 交互规则：没有复制；macOS 无选择 Delete 仍进入清空流程，只是先确认。
- 配色、间距、圆角、图标：没有修改。
- 选择、键盘和拖选规则：没有修改；只在既有清空函数入口处延迟执行清空。

结论：本份没有越过「只同步功能，不同步 UI / 美术风格 / 交互逻辑」边界。
