# ClipShelf for Windows

A native Windows clipboard-history app, adapted from [LoganPage/ClipShelf](https://github.com/LoganPage/ClipShelf).

Windows 原生历史剪贴板工具：保留 Mac 版的图标、简洁列表和浅深色外观，选择、快捷键、托盘和窗口行为按 Windows 使用习惯适配。

## 当前发布：1.0.22 体验版

建议先备份数据再试用。本版本适合公开测试，不代表已完成全部 Windows 硬件、显示缩放和文件格式兼容性验证。

- 预览中上下键切换记录，左右键切换 PDF 页面、PPT 幻灯片或 Excel 工作表；空格/Esc 关闭，不自动粘贴。
- 翻页保留当前内容，缓存最近四页并有限预读；大文件首次读取仍可能较慢。
- Excel 读取前 500 行而非加载完整工作表；大共享字符串表、复杂格式或加密内容仍可能超出预览限制。
- 标题栏独立细线按钮、聚焦状态灯、统一滚动条粗细与默认/悬停/拖动配色。
- 修复长截图缩略图仅限制宽度的问题，限制解码宽高；高刷新率屏幕上的实际流畅度仍需要更多设备验证。

### 已知限制与下一步

1. Office 是内容预览，不还原原排版、图片和图表；旧 DOC/XLS/PPT 暂不支持正文预览。
2. 宽表格的横向滚动样式仍需完善；需要查看完整表格时请使用默认应用打开。
3. PDF 首次打开与媒体编码支持取决于文件复杂度及系统组件，不承诺秒开或固定帧率。
4. 当前没有签名安装器、内置自动更新或 ARM64 独立包。更新需正常退出后覆盖安装。
5. 自动化检查采用隔离合成数据，不能替代真实多显示器、屏幕阅读器和长期使用验证。

后续优先项：宽表导航、复杂 PDF 加载、真实高刷/多 DPI 性能验证，以及安装签名与更新机制。

## 功能

- 保存文字、文件和图片历史；搜索支持中文、拼音及首字母。
- 新复制的多个文件按一文件一记录保存；单次批量导入只保存、刷新一次，重复路径去重并保留置顶，仍遵守历史条数上限。旧的合并记录不自动迁移。
- PDF、电子表格、Word、演示文稿、压缩包、文件夹等有独立 Fluent 类型图标，支持浅深色；基于微软 Fluent UI System Icons（MIT），详见程序内 ThirdPartyNotices.txt。
- 置顶、批量复制、连续拖选、撤销最近删除。
- 内置文字和图片预览；监听新截图并保留截图原文件。
- 文件支持图片、分段文本、PDF 页面、Word 正文、Excel 工作表、PPT 逐页文字、CSV/TSV 表格、ZIP 与文件夹清单；音视频手动播放（依赖系统解码器）。
- 跟随系统或手动切换浅色/深色，四款图标及可选选中颜色。
- Windows 风格菜单、分组设置卡片、开关、系统圆角、连续滚动和列表虚拟化。
- 全局快捷键、托盘驻留、可选开机启动。

## 安装

建议使用 Windows 11 x64。如果你取得的是 Windows 独立运行包，解压后打开 `ClipShelf/ClipShelf.exe`，无需另装 .NET；如果取得的是源码，请先按下方“从源码构建”生成程序。

安装到当前用户目录并创建桌面、开始菜单快捷方式：

```powershell
.\install.ps1
```

安装位置为 `%LOCALAPPDATA%\Programs\ClipShelf`，不需要管理员权限。更新前从托盘选择“退出”，再用新版安装脚本覆盖安装。安装程序不会清除历史或设置。

## 使用

| 操作 | 快捷键/交互 |
| --- | --- |
| 显示 ClipShelf | `Ctrl+Shift+V`，可在设置中修改 |
| 选择记录 | 单击、`Ctrl+点击`、`Shift+点击`、按住拖选 |
| 搜索 | `Ctrl+F` |
| 复制选中记录 | `Ctrl+C` 或复制按钮 |
| 预览 | `Space` |
| 置顶 / 取消置顶 | `Ctrl+Shift+P` |
| 删除选中记录 | `Delete` |
| 撤销最近删除 | `Ctrl+Z`，本次运行内最多十批 |
| 打开菜单 | 右键或 `Shift+F10` |

搜索框中的快捷键保持普通文字编辑行为。关闭窗口会隐藏到托盘；左键单击托盘图标打开主界面，右键可暂停记录、打开设置或退出。

在“设置 → 历史与选择”可开启“再次点击已选记录时取消选中”（默认关闭）。开启后，再次点击唯一选中的记录会在松开鼠标时取消选择；多选时单击仍先收拢为一条，Ctrl/Shift、方向键和拖选规则不变。开关立即保存，重启后保留。

置顶状态由原大头针图标变色表示，记录行不再显示“已置顶”标签。设置使用浅深色分组卡片和连续滚轮响应，点击、导航或关闭时可随时中断；遵守系统减少动画设置，不固定帧率。

设置开关原地更新，不会把页面拉回顶部。关闭再打开保留本次运行内的位置；面板统一淡入，开关圆点使用可打断的轻量过渡，系统关闭动画时即时切换。

设置页的单行输入框、未展开下拉框与空白区域统一处理滚轮，跨控件不会切换成逐行跳动，也不会误改下拉设置。较大的非标准滚轮增量连续过渡，混合细小增量不丢失进行中的滚动目标。

先在 ClipShelf 选择记录并复制，再到需要输入的位置按 `Ctrl+V` 粘贴。ClipShelf 不自动切换应用、不模拟按键、不因复制隐藏窗口。记录上的 `Space` 仅打开预览；`Enter` 和 `Ctrl+V` 不再执行记录命令。搜索框仍可正常粘贴文字。

截图监听只接收启动监听后新建或改名进入的图片，不扫描导入旧截图。`Win+Shift+S` 复制的图片由剪贴板监听记录。

## 隐私与数据

文件预览按 Space 打开，再按 Space / Esc 关闭。图片与 PDF 可缩放；PDF、工作表和幻灯片有分页按钮。文本首段 128K 字符，可继续加载至 1M。DOCX/DOCM/ODT 提取正文，PPTX/PPTM 提取逐页文字，不还原原排版或图片；XLSX/XLSM 显示每表前 500 行、50 列，使用已有公式结果，不计算公式、不执行宏或外部连接。CSV/TSV 显示首段中的前 500 行、50 列。ZIP 与文件夹最多列出 500 项，不解压、不递归。旧版 DOC/XLS/PPT、RAR/7z 等仍显示信息。

常见音视频（MP3、WAV、MP4 等）必须点击播放才载入，关闭或切换预览会停止；具体容器与编码、WebP/HEIC 图片是否可解码取决于系统组件。超过 100 MB 的 PDF 或 Office/ZIP 包使用信息预览，包内单个 XML 限 4 MB；损坏、加密或缺少解码器时显示提示。预览只读，不自动启动外部程序，原文件移动后不会保留隐式副本；不自动访问网络路径。执行类文件、脚本和快捷方式不提供直接运行入口。

剪贴板历史仅保存在本机，没有账号、服务器同步或云端上传。历史、设置和图片缓存位于 `%LOCALAPPDATA%\ClipShelf`。剪贴板中可能包含敏感信息，请按需暂停记录或删除历史。程序识别其支持的系统敏感内容排除标记，但不能保证每个来源应用都提供该标记。

删除历史不会删除用户的原始文件或截图。备份时请先正常退出，再复制整个数据目录。正常退出会等待保存完成；强制结束进程或断电仍可能丢失尚未落盘的记录。

## 从源码构建

需要 Windows 10 2004 或更新版本（推荐 Windows 11）和 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。项目使用 WPF / Windows Forms，PDF 使用 Windows.Data.Pdf；构建时会还原微软 Windows SDK .NET 引用。独立运行包无需安装 SDK。

```powershell
.\build.ps1 -SelfContained
```

输出为 `dist/ClipShelf`。应用沿用四款原始 PNG 并按 Windows 图标比例显示；构建脚本为安装用默认图标生成 16–256 像素的多尺寸 ICO，不改动原始图案。

也可直接构建和发布：

```powershell
.\generate-icons.ps1
dotnet publish .\ClipShelf\ClipShelf.csproj -c Release -r win-x64 --self-contained true -o .\dist\ClipShelf
```

制作便携安装包时，将 `dist/ClipShelf` 文件夹与根目录的 `install.ps1`、`README.md`、`LICENSE` 放在同一目录后压缩即可。此仓库不包含私人历史、截图、机器相关测试输出或已编译程序。

## 验证

源码包含隔离的合成数据检查，可在构建后的程序上使用：

```powershell
.\dist\ClipShelf\ClipShelf.exe --ui-performance-test "$PWD\artifacts\ui-performance.json"
.\dist\ClipShelf\ClipShelf.exe --interaction-test "$PWD\artifacts\interaction.json"
.\dist\ClipShelf\ClipShelf.exe --drag-test "$PWD\artifacts\drag"
.\dist\ClipShelf\ClipShelf.exe --focus-test "$PWD\artifacts\focus"
.\dist\ClipShelf\ClipShelf.exe --copy-only-test "$PWD\artifacts\copy-only"
.\dist\ClipShelf\ClipShelf.exe --settings-test "$PWD\artifacts\settings"
```

每次运行一个检查，等待对应报告写入后再进行下一个；报告的 `passed` 字段表示结果。这些检查使用独立合成数据，不读取日常剪贴板历史。

完整 `--self-test` 会访问真实剪贴板，验证文字、图片、文件复制及截图监听，并尝试恢复原剪贴板；不包含跨应用粘贴或模拟按键。不要将完整自测用于正在处理敏感内容的日常桌面。

## 致谢与许可

Mac 上游：[LoganPage/ClipShelf](https://github.com/LoganPage/ClipShelf)，参考提交 `0ab74e8338b720409349b6f627de0d6d1f4013c5`。感谢原项目提供设计、图标及行为参考。Windows 版本是独立实现，系统能力和交互细节不保证与 macOS 完全相同。

使用 [MIT License](LICENSE)，保留原有 `Copyright (c) 2026 Applebook743` 版权声明。此项目与 Microsoft、Apple 无隶属关系。
