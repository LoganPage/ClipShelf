# prompt-37 验证报告：文件类型差异化图标

## 实现

- 文件记录按 PDF、表格、Word、演示文稿、压缩包、文件夹和通用文件分类。
- 七种分类使用互不相同的 SF Symbol；未知扩展名、无扩展名和缺失路径回退通用文档图标。
- 多文件旧记录按首个路径分类。
- 每种分类均提供独立浅色和深色前景、背景配色。
- 扩展名判断为纯内存逻辑；文件夹属性在 SwiftUI `.task` 的后台任务中读取，绘制路径不做磁盘访问。
- 文件夹判断按路径缓存，同一路径最多进行一次属性读取，不读取文件内容、不调用 Shell。

## 自动验证

- `swift build`：通过。
- `swift test`：通过，最终共享测试 `32/32`。
- `ClipShelf --self-test`：退出码 0，最终共享检查 `32/32`。
- 覆盖：`.pdf`、`.xlsx`、`.docx`、`.pptx`、`.zip`、文件夹、未知扩展名、无扩展名、空路径、图标唯一性和目录缓存只读取一次。

## 实际界面验证

- 已安装版本正常渲染既有文件记录；旧多文件记录按首路径或通用回退显示。
- 文件缩略图仍保持 `52 x 40` 容器和圆角 10，行高、分隔线及文字/图片缩略图未改动。
- 浅色与深色颜色均由 `AppTheme` 动态颜色提供，随现有外观模式切换。

## 改动文件

- `Sources/ClipShelfLite/Support/FileTypeIcon.swift`
- `Sources/ClipShelfLite/Support/AppTheme.swift`
- `Sources/ClipShelfLite/Views/MainView.swift`
- `Sources/ClipShelfLite/Support/SelfTest.swift`
- `README.md`
