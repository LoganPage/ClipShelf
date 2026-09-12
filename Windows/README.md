# ClipShelf for Windows

Windows 原生历史剪贴板，基于 [LoganPage/ClipShelf](https://github.com/LoganPage/ClipShelf) 的 Mac 版设计与图标移植。当前版本：1.1.1 体验版。推荐 Windows 11，最低 Windows 10 2004，x64；自包含包无需另装 .NET。

## 本次更新

- Excel 等不支持的文件按 Space 也会打开快速预览窗口，中央显示“不支持快速预览”提示、完整文件位置，以及默认应用打开/在资源管理器中显示按钮。不读取这些文件的正文。
- 主列表、设置和预览的共享滚动条向右移动 2 DIP，保留原有粗细、拖动命中范围和悬停/拖动颜色。
- 保留文字、剪贴板图片及常见图片预览。PDF 直接按页渲染；DOCX/PPTX 使用本地原生 OOXML 解析，不启动 Office/LibreOffice，不转 PDF，不用 Windows Preview Handler。
- 翻页缓存、相邻页预加载、后台增量分页、请求取消与过期结果拦截；切页保留上一帧，减少白屏闪烁。

## 安装与使用

完整解压 Windows 发布 ZIP，运行 ClipShelf/ClipShelf.exe。需要桌面/开始菜单快捷方式时，在解压目录运行 `powershell -ExecutionPolicy Bypass -File .\install.ps1`。仅安装当前用户，无需管理员权限。升级前从托盘正常退出并备份 `%LOCALAPPDATA%\ClipShelf`；覆盖安装保留历史和设置。

Ctrl+Shift+V 呼出窗口；Ctrl+C 复制选中记录，再到目标应用手动粘贴。支持中文/拼音搜索、置顶、多选、删除撤销、截图文件夹监听、托盘和浅深色主题。关闭按钮行为可在设置中选择托盘或退出。

Space 打开/关闭预览，Esc 关闭；↑/↓ 切换可预览记录，←/→ 翻文档页，Home/End 首末页，滚轮只滚动内容。文字和图片保留；图片多帧目前显示首帧。DOC/PPT、Excel、媒体、ZIP、文件夹等只显示说明和文件位置；完整内容交给默认应用。

## 原生文档预览与限制

PDF 使用 Windows 本地 PDF 引擎。DOCX 支持常见段落/标题/样式、字体、列表、图片块、基础表格和普通页眉页脚，采用增量分页检查点。PPTX 支持主题/母版/版式基本继承、文字框、图片、基础形状/旋转/透明度和表格；有内嵌缩略图时先显示缩略图。

Word/PPT 明确标记“近似预览”，不是完整 Office 排版。复杂浮动对象、合并表格、多节页设置、图表、SmartArt、公式、组合形状等简化或显示占位。动画、宏、外部关系和嵌入程序不执行。DOCX 正文 XML 会先安全读取，后续解释与分页按需执行，并非完全流式引擎。

缓存分为元数据、模型、分页/场景和位图；位图与模型各有 64 MiB 预算，磁盘页缓存 512 MiB / 14 天，文件修改自动失效。ZIP/XML、页数、图片像素均有安全上限；超限或损坏在内容区提示，不弹阻塞窗口。用户文件不上传、不联网处理。

仍是未签名体验版，无内置自动更新。PDF 引擎冷启动可能超过 300ms；大/复杂文档、高刷新率、多屏和各 DPI 实机仍需更广泛验证。不承诺固定 FPS 或与 Office 一致的页数。任意倍率缩放后的独立高清渲染层尚未实现。

## 开发与验证

使用 .NET 8 SDK：`dotnet publish ClipShelf/ClipShelf.csproj -c Release -r win-x64 --self-contained true`。

`--native-preview-test <输出目录>` 生成合成 PDF/DOCX/PPTX，检查格式、布局、样式、安全、取消、缓存和键盘。`--file-preview-test <输出目录>` 检查文字图片和文档预览回归。`--scroll-render-test <报告.json>` 与 `--preview-scroll-test <报告.json>` 检查列表滚动及后台分页时的滚动。合成截图/DPI 和渲染回调不等于物理多屏/高刷验收。

当前部分旧测试仍期待四套图标、旧圆角/滚动条尺寸或即时主题切换，已在旧版复现失败，不应视作全量测试已通过。预览专项和本次修改单独验证。

仅发布源代码、图标、授权文件及干净构建产物；不包含用户历史、设置、缓存、截图、测试报告或本机备份。许可证见 LICENSE 和 ClipShelf/ThirdPartyNotices.txt。
