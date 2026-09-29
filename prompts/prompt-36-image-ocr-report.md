# prompt-36 验证报告：图片记录按需 OCR

## 结果

完成。图片预览顶部新增按需 OCR 工具条，使用系统 Vision 在后台识别，支持大图降采样、进度/耗时反馈、复制全部及按图片坐标拖框复制所选文字。

## 改动文件

- `Sources/ClipShelfLite/Services/ImageTextRecognizer.swift`
- `Sources/ClipShelfLite/Services/PreviewController.swift`
- `Sources/ClipShelfLite/Support/SelfTest.swift`
- `README.md`

## 自动测试

- `swift build --disable-sandbox`：通过，无编译警告。
- `swift test --disable-sandbox`：通过，`37/37` 检查通过。
- `.build/debug/ClipShelf --self-test /tmp/clipshelf-p36-self-test.md`：退出码 0，`37/37` 检查通过。
- 新增断言：语言配置为 `zh-Hans / en-US`、启用语言纠正、空白过滤、重复区域去重、最佳置信度保留和阅读顺序。
- 既有 35 条断言未删除、未改写。

## 手工验证步骤

1. 打开真实图片预览但不点击按钮，确认没有进度状态、CPU 峰值或 Vision 请求；工具条提示“按需识别”。
2. 点击顶部“识别文字”，确认预览仍可滚动和关闭，识别中显示转圈与大图优化提示。
3. 使用包含中英文的真实图片，确认完成后显示段数、耗时，并可“复制全部”。
4. 在 50%、100%、200% 和 400% 缩放下分别拖框，确认只选中框内文字，“复制所选”内容一致。
5. 使用无文字图片，确认显示“未识别到文字”，窗口不阻塞、不崩溃。
6. 使用 4000×3000 图片，确认出现“已优化大图”反馈；识别途中关闭预览，确认任务取消且无残留覆盖层。

本轮未覆盖安装，未执行真实图片 UI OCR，因此未附识别截图；以上项目保留给手工验收，不标记为已通过。

## 范围说明

- OCR 入口严格位于顶部工具条，缩放控件仍在右下角。
- 拖框将 Vision 归一化图片区域映射到 `NSImageView` 的实际等比显示矩形，不使用窗口屏幕坐标。
- 未改 `showBuiltInPreviewIfPossible(_:)`、剪贴板捕获路径或 `history.json`。
- 识别仅使用本地 Vision，不联网、不引入第三方依赖。
