# prompt-42 验证报告：文本预览增强

## 结果

完成。内置文本预览新增行号、自动换行、查找条和编码状态；常见文本文件改用内置预览，非文本文件继续回落 Quick Look。

## 改动文件

- `Sources/ClipShelfLite/Support/TextEncodingDetector.swift`
- `Sources/ClipShelfLite/Services/TextPreviewSession.swift`
- `Sources/ClipShelfLite/Services/PreviewController.swift`
- `Sources/ClipShelfLite/Support/SelfTest.swift`
- `README.md`

## 自动测试

- 首次构建发现并修复三处编译问题：GB18030 常量导入、`NSSearchFieldDelegate` 协议、`CGFloat` 无限宽度类型推断；该次旧二进制自检不计入结果。
- 修复后 `swift build --disable-sandbox`：通过，无编译警告。
- `swift test --disable-sandbox`：通过，`39/39` 检查通过。
- `.build/debug/ClipShelf --self-test /tmp/clipshelf-p42-self-test.md`：退出码 0，`39/39` 检查通过。
- 新增断言：UTF-8 BOM、UTF-16 LE/BE、GB18030 中文解码，以及文本扩展名与 PDF/Office/图片回落边界。
- 既有 37 条断言未删除、未改写。

## 手工验证步骤

1. 打开文字记录，切换“行号”和“自动换行”，确认行号栏与横向滚动即时变化。
2. 按 `Cmd+F` 打开查找条，输入重复关键词；按 F3、Shift+F3 前后循环跳转。
3. 查找条打开时按 Esc，只关闭查找条且预览保留；查找条关闭后按 Esc，应由原响应链关闭预览。
4. 打开 UTF-8 BOM、UTF-16 LE/BE、GB18030 中文文件，确认工具条编码名称和正文无乱码。
5. 打开 20 MB `.log`，确认先显示读取状态、仅分段载入前 4 MB、窗口可滚动和关闭。
6. 验证 `.txt/.md/.json/.csv` 使用内置文本预览；`.pdf/.docx/.pptx/.xlsx` 使用 Quick Look；图片文件仍使用图片预览。
7. 回归 `Cmd+=`、`Cmd+-`、`Cmd+0`、`Cmd+滚轮` 及 Space/左右方向键。

本轮未覆盖安装，真实窗口交互与 Esc 响应链行为保留给手工验收，不标记为已通过。

## 范围说明

- `.file` 分支先尝试 `NSImage(contentsOf:)`，随后才判断文本扩展名；`.image` 分支未改。
- 文本文件以 256 KB 分段读取，预览最多载入 4 MB，不一次读取整份大文件。
- `Cmd+F / F3 / Shift+F3 / Esc` 与第 41 项缩放按键共存；第 41 项滚轮监听清理未改。
- 编码和界面状态不写入 `history.json`。

