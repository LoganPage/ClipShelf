# 批次 E 实施小结

## 完成状态

| 顺序 | 规格 | 状态 |
| --- | --- | --- |
| 1 | prompt-47 版本单一来源与窗口标题 | 已完成 |
| 2 | prompt-49 清空确认与记录右键菜单 | 已完成 |

按 `47 → 49` 顺序实施。prompt-48 按批次要求显式跳过，没有修改其边框美术项。

## 断言变化

- 批次开始：41 条。
- prompt-47：新增 1 条聚合断言，42 条。
- prompt-49：新增 2 条断言，44 条。
- 最终连续三次：`Result: 44/44 checks passed.`

`SelfTest.swift` 的本批 diff 只有 31 行追加，全部位于 `built-in text preview extension routing is narrow` 之后、收尾断言 `self test defaults suite file count does not grow` 之前。既有断言没有修改、删除或移动，defaults 收尾断言仍为最后一条。

## 总体验证

```text
swift build --disable-sandbox
Build complete!

swift test --disable-sandbox --scratch-path /tmp/cs-batch-e
Test Suite 'All tests' passed.

三轮 --self-test
Result: 44/44 checks passed.

tests/parity/runtime_control.sh
通过 15   失败 0

CLIPSHELF_PARITY_ISOLATED=1 ... tests/parity/run.sh
通过 29   失败 0   跳过 1
```

打包应用真实窗口标题为 `ClipShelf 1.4.1`，裸二进制为 `ClipShelf`。右键菜单和两个可定位 UI 清空入口完成了隔离数据界面实测；人工覆盖边界详见 prompt-49 报告。

## 分工与红线核对

- `AppDelegate.swift`：47 只改窗口标题；49 只改 `clearHistory()`。
- `SelfTest.swift`：47 断言在前，49 两条断言紧随其后，defaults 收尾断言保持最后。
- `RuntimeControlServer.swift`：47 只改版本来源；49 的 `--ctl clear` 未改。
- `ClipStore.clearHistory()`：未改。
- `Package.swift`、`tests/parity/`、`docs/`、`README.md`、`Assets/`：未改。
- `handleRowClick`、`handleKey`、`handleCommandKey`、`DragSelectionCaptureView`、`KeyboardCaptureView`：未改变行为。

为避免版本标题导致主窗口键盘事件失效，`shouldHandleMainWindowKeyEvent` 的标题判定从完全相等改为接受 `ClipShelf` 前缀。这是保持既有键盘功能所需的兼容修复，没有改变任何键位或选择规则。

## 功能边界自查

本批没有复制 Windows 的视觉、菜单样式、自绘弹窗、键位或选择规则。新增内容只有版本显示、原生清空确认和原生记录右键菜单。因此没有越过「只同步功能」这条线。

## 遗留问题

- System Events 缺少辅助功能权限，窗口标题证据改由只读 Computer Use 获取。
- 状态栏入口、Delete 键以及确认框破坏性按钮仍建议用户进行一次手工复核；自动测试已覆盖其共用判定、清空与撤销语义。
- 对齐测试的 T12 按脚本默认配置跳过；本批已独立完成两次 App bundle 构建与签名验证。
