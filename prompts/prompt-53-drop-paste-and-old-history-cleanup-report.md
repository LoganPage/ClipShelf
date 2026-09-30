# prompt-53 实施报告：移除自动粘贴并新增旧历史清理

## 完成状态

已完成 R1、R4 和 G1：自动粘贴及全部入口已删除，行内“已置顶”文字已删除，设置中新增按 7 / 30 / 90 天查看并清理旧的未置顶记录。清理复用现有批量删除和撤销栈，不删除原文件。

## 改动清单

### 新增

```text
50  0  Sources/ClipShelfLite/Support/OldHistoryCleanup.swift
```

### 修改

```text
 0  9  Sources/ClipShelfLite/App/AppDelegate.swift
11 10  Sources/ClipShelfLite/Stores/ClipStore.swift
 1  4  Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift
61  3  Sources/ClipShelfLite/Support/SelfTest.swift
 4 56  Sources/ClipShelfLite/Views/MainView.swift
69  0  Sources/ClipShelfLite/Views/SettingsView.swift
```

### 删除

```text
 0 20  Sources/ClipShelfLite/Services/PasteController.swift
```

`Package.swift` 使用 `path: "Sources/ClipShelfLite"` 目录通配，因此新增和删除 Swift 文件均无需修改包清单。

## A：构建与测试

### `swift build --disable-sandbox`

```text
Building for debugging...
[Planning deferred tasks]
[5/10] ClipShelf-product
[14/19] ClipShelf-product
[15/19] ClipShelf-product
[17/19] ClipShelf-product
Build complete! (5.95秒)
```

### `swift test --disable-sandbox --scratch-path /tmp/cs-p53-final.eJ9CUI`

```text
Build complete! (16.31秒)
[PASS] record context menu exposes the declared actions in order: The installed native row menu derives copy, pin, and destructive delete from one declaration
[PASS] old history cleanup selects only unpinned records older than the window: Only an unpinned record strictly older than the cutoff is eligible
[PASS] old history cleanup retention window only accepts the offered values: Retention accepts 7, 30, or 90 days and otherwise uses the 30-day default
[PASS] old history cleanup summary reports the eligible count: Cleanup summaries distinguish an empty result and include both days and item count
Test Suite 'All tests' passed at 2026-10-01 02:09:45.330.
✔ Test run with 0 tests in 0 suites passed after 0.001 seconds.
exit=0
pass_lines=94
fail_lines=0
```

两条测试桥接路径各执行 47 条断言，共 `94 = 2 × 47` 条 PASS。

### 独立 `--self-test`

```text
exit=0
Result: 47/47 checks passed.
- [x] record context menu exposes the declared actions in order: The installed native row menu derives copy, pin, and destructive delete from one declaration
- [x] old history cleanup selects only unpinned records older than the window: Only an unpinned record strictly older than the cutoff is eligible
- [x] old history cleanup retention window only accepts the offered values: Retention accepts 7, 30, or 90 days and otherwise uses the 30-day default
- [x] old history cleanup summary reports the eligible count: Cleanup summaries distinguish an empty result and include both days and item count
```

## B：删除完整性

在仓库根目录执行规格中的六条检查，原始输出：

```text
--- grep 粘贴 ---
零命中 ✅
--- grep PasteController ---
零命中 ✅
--- grep store.paste ---
零命中 ✅
--- grep suppressPasteUntil ---
零命中 ✅
--- grep handlePaste ---
零命中 ✅
--- deleted file ---
ls: Sources/ClipShelfLite/Services/PasteController.swift: No such file or directory
文件已删除 ✅
```

删除范围包括：顶部按钮、行内按钮、行右键菜单项、菜单栏最近记录子菜单项、Return / 小键盘 Enter 路径、Cmd+V 记录命令、两个吞键列表中的 `v`、`ClipStore.paste` 两个重载和 `PasteController` 文件。菜单栏最近 8 条记录子菜单本身与“复制”项保留。

## C：Cmd+V 与 Return 人工验收

使用临时数据目录、隔离 defaults suite 和独立 pasteboard 启动当前构建的 App。

### 搜索框 Cmd+V

聚焦搜索框后执行系统粘贴，Accessibility 原始关键输出：

```text
12 text field (settable) Value: p53-cmd-v-search, Placeholder: 搜索文字、文件名、截图名
The focused UI element is 12 text field (settable) Value: p53-cmd-v-search
```

结果：文本进入搜索框，窗口没有隐藏，也没有执行历史记录操作。

### 列表 Cmd+V 与 Return

清空搜索并让列表获得焦点后依次按 Cmd+V、Return，原始关键输出：

```text
Window: "ClipShelf 1.4.1", App: ClipShelf.
12 text 已选 1 条
13 text field (settable) 搜索文字、文件名、截图名
23 text old-pinned
28 text new-unpinned
33 text old-unpinned
38 text original.txt
```

结果：窗口继续显示，搜索框为空，四条记录未改变，没有执行粘贴或隐藏窗口。源码检查也确认 `KeyCaptureNSView` 不再吞 Cmd+V、Return 或小键盘 Enter。

## D：清理旧历史人工验收

隔离数据准备：

```text
HISTORY_BEFORE=[["old-unpinned", false], ["old-pinned", true], ["new-unpinned", false], ["original.txt", false]]
```

其中前两条及文件记录均为 40 天前，新记录为 2 天前。

### 默认值与数量预览

设置页原始关键输出：

```text
57 pop up button Description: 保留最近, Value: 30 天
58 button 查看待清理记录数量
59 button (disabled) 确认清理这些记录
```

点击查看后：

```text
59 text 将清理 30 天前的 2 条未置顶记录。
60 button 查看待清理记录数量
61 button 确认清理这些记录
```

旧的置顶记录未计入，新的未置顶记录未计入，符合预期。

### 确认清理

经用户确认后，仅对隔离临时记录执行清理。界面原始关键输出：

```text
58 text 已清理 2 条记录。
59 button 查看待清理记录数量
60 button (disabled) 确认清理这些记录
84 text old-pinned
89 text new-unpinned
```

磁盘验证：

```text
after_cleanup_titles=["old-pinned", "new-unpinned"]
original_file_exists=yes
```

### Cmd+Z 整批恢复

关闭设置后按一次 Cmd+Z，界面重新出现四条记录。磁盘原始输出：

```text
after_undo=[["old-pinned", true, 809028196.942622], ["new-unpinned", false, 812311396.942622], ["old-unpinned", false, 809028196.942622], ["original.txt", false, 809028196.942622]]
original_file_exists_after_undo=yes
```

结果：两条被清理记录一次性恢复；原 ID 对应的时间与置顶状态由既有撤销栈原样保存；原文件清理前后均存在。

## E：隔离黑盒回归

执行：

```text
CLIPSHELF_PARITY_ISOLATED=1 CLIPSHELF_PARITY_DELIVERED=31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49 tests/parity/run.sh
```

最终原始关键输出：

```text
✔ PASS T11 --self-test 退出码 0，报告已生成
✔ PASS T11 --self-test 报告覆盖已交付提示词（31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49）的全部主题
Result: 47/47 checks passed.
✔ PASS T16 上限 10000 时进程未崩溃
✔ PASS T16 上限 10000 时 8 条全部累积
── 汇总 ──
通过 29   失败 0   跳过 1
```

唯一跳过项是默认关闭的 T12 打包冒烟。隔离校验确认用户真实 `history.json` 未被修改；`tests/parity/` 没有改动。

## 断言计数与名称对照

```text
before_assertions=44
after_assertions=47
```

断言名称差异：

```text
removed:
added:
old history cleanup selects only unpinned records older than the window
old history cleanup retention window only accepts the offered values
old history cleanup summary reports the eligible count
```

原有断言名称没有删除或改名。目标断言名称仍为：

```text
record context menu exposes the declared actions in order
```

它的判据仅按本规格移除 `.paste`，并继续验证声明顺序、标题、真实菜单挂载和破坏性删除角色。其余 43 条既有断言未改。

## 实现核对

- `OldHistoryCleanup` 只接受 7 / 30 / 90，其他值回落 30。
- 截止时间按每天 86400 秒计算，只有严格早于截止时间且未置顶的记录入选。
- `ClipStore.removeOldUnpinnedItems` 只计算 ID 并调用既有 `remove(ids:)`，没有直接修改 `items` 或撤销栈。
- 设置页只有用户主动查看和确认时才计算、清理，不做定时或伴随式自动清理。
- 清理状态为空时确认按钮不可用，切换保留天数后会清空旧预览结果。
- `copy(_:)` 两个重载及全部 pasteboard 写入路径未修改。
- Runtime Control、历史撤销栈、条数上限行为均未修改。

## 只删功能、不碰美术风格

本次没有修改任何现有配色、间距、圆角或图标定义，也没有改 `AppTheme`、Assets 或图标脚本。主界面只删除了冗余按钮和“已置顶”文字；新增设置区块完全使用项目现有 `settingsSection`、原生 `Picker`、`Button` 和 `Text`。行内大头针按钮、填充状态、tooltip 和无障碍名称均保持原样。
