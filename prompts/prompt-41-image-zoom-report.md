# prompt-41 验证报告：图片预览缩放

## 结果

完成。图片预览以“适配窗口”为 100%，支持 50%–400% 缩放、视口中心锚定、右下角控制浮层、键盘快捷键和 `Cmd+滚轮`。

## 改动文件

- `Sources/ClipShelfLite/Services/PreviewController.swift`
- `Sources/ClipShelfLite/Support/SelfTest.swift`
- `README.md`

## 自动测试

- `swift build --disable-sandbox`：通过。
- `swift test --disable-sandbox`：通过，`35/35` 检查通过。
- `.build/debug/ClipShelf --self-test /tmp/clipshelf-p41-self-test.md`：退出码 0，`35/35` 检查通过。
- 新增断言：缩放下限 0.5、上限 4.0、越界夹紧、步进夹紧和复位到 1.0。
- 既有 34 条断言未删除、未改写。

## 手工验证步骤

1. 打开图片预览，确认默认适配窗口且右下角显示 `− / 复位 / 100% / +`。
2. 使用按钮、`Cmd+=`、`Cmd+-`、`Cmd+0` 与 `Cmd+滚轮`，确认范围为 50%–400%，百分比即时变化。
3. 放大后滚动到图片局部，再继续缩放，确认视口中心附近内容保持稳定，不跳回左上角。
4. 调整预览窗口大小，确认当前缩放画布重新适配；关闭再打开应回到 100%。
5. 验证 Space、Esc、左右方向键、文字预览和 Quick Look 行为不回退。
6. 用 4000×3000 图片放大到 400%，确认滚动、关闭无卡死。

本轮未覆盖安装，未保存真实窗口截图；图形交互保留给手工验收。

## 范围说明

- 缩放控件严格位于右下角，顶部区域留给 OCR。
- `.image` 分支归本项；文字和文件回落逻辑未改。
- `scrollWheelMonitor` 与 `keyMonitor` 均在 `stopKeyMonitor()` 移除。
- 缩放状态仅存在于当前预览会话，不写入 `history.json`。
