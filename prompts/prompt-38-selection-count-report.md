# prompt-38 验证报告：当前选中数量

## 实现

- 搜索栏右侧显示“已选 N 条”，计数直接来自 `selectedIDs.count`，没有新增计数状态。
- 0 条时标签完全隐藏，不显示“已选 0 条”。
- 标签使用固定 92pt 宽度并覆盖在搜索栏内部，不参与搜索栏布局；出现、消失和数字位数变化不会挤动搜索框。
- 文字沿用 caption 字号和 secondary 颜色。

## 自动验证

- `swift build`：通过。
- `swift test`：通过，最终共享测试 `32/32`。
- `ClipShelf --self-test`：退出码 0，最终共享检查 `32/32`。
- 覆盖：0、负数、1、3、300 条的隐藏与文案规则。

## 实际界面验证

- 无选择时未显示计数。
- 单选一条后辅助功能树和界面均显示“已选 1 条”。
- 切换记录类型后计数立即消失，未残留旧值。
- 搜索框在计数出现前后保持同一布局。

## 改动文件

- `Sources/ClipShelfLite/Support/SelectionCountLabel.swift`
- `Sources/ClipShelfLite/Views/MainView.swift`
- `Sources/ClipShelfLite/Support/SelfTest.swift`
- `README.md`
