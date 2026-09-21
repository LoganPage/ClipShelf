# ClipShelf 性能与界面修正记录

## 1.0.11 置顶视觉与设置页

记录行删除PinnedLabel，以原E718大头针的AccentBrush区分置顶；顶部按钮在全部选中记录已置顶时同步强调。PinGlyphTemplate明确将字形Foreground绑定到按钮，避免应用隐式TextBlock样式覆盖按钮颜色。取消置顶恢复中性色，混合选择不显示全部已置顶；悬停和无障碍名称仍说明操作，不改变置顶排序或存储格式。

设置改为固定标题/底栏、浅深色分组卡片、细边线、40DIP图标选择与CheckBox语义的开关。开关保留Tab/Space和ToggleProvider状态，保存监听Checked/Unchecked，包含无障碍切换而不仅是鼠标Click。主题/图标选择刷新现有控件，不重建焦点树。布局参考[Microsoft设置指南](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings)，视觉继续Win11；Apple Design技能仅用于连续反馈、按实际时间推进和可中断交互原则。

新增SmoothScrollViewer接入既有WheelScrollMotion，普通滚轮经CompositionTarget.Rendering按Stopwatch实际时间推进，精细delta和减少动画直接响应。通过ScrollContentPresenter的公开IScrollInfo修改实际偏移，不使用每帧UpdateLayout、RenderTransform伪滚动或固定60Hz计时器。鼠标/键盘/触控输入、失活、隐藏/卸载、布局范围改变和外部导航会取消旧目标；嵌套编辑器/选择框保留原生处理。将设置阴影移到静态兄弟Border，滚动控件的祖先无Effect；设置滚动条明确MinWidth=0，防止系统最小宽度撑大细滚动条。

Release构建、自包含发布及56文件公开源码独立发布均成功。自动检查通过：新增设置136项、交互156项、焦点54项、拖选43项、外观596项、布局773项、UI性能50项。公开运行包另通过设置136项。设置检查包含初始置顶、实际字形颜色、混合选择、开关无障碍状态和保存、680/900/1200宽度及浅深色截图、连续物理偏移、精细输入、中断/外部跳转、上下边界和回调清理。Computer Use仅操作独立示例窗口，实际检查设置打开和滚轮、普通记录选择后点击顶部置顶，目视确认行内与顶部大头针变蓝、无额外标签。测试与截图没有访问日常历史，也没有测量实际显示FPS。

正常退出后备份至`before-1.0.11-20260909-110650`；覆盖安装前后23个用户数据文件逐个SHA256与备份相同。安装exe/dll/ICO与发布产物一致，1.0.11已重新打开且保持响应。公开源码ZIP在构建前生成，仅含56个白名单文件；公开运行包467项，排除本机报告、用户历史、测试截图与PDB。本轮只更新本地公开候选包，未执行GitHub上传。

## 1.0.10 可选重复单击取消选择

新增DeselectOnRepeatedClick，默认false，旧设置不含字段时保持原有选择行为。设置历史区的开关直接保存，不重建控件树、不重新应用主题或监听器；延迟保存的CopySettings深快照也复制此字段，重启后保留。

开关仅作用于无修饰键点击唯一已选记录。首次按下只建立待处理ID，不发出空选择事件；同一行内松开且仍是该条唯一选中时提交一次取消，保留键盘位置与Shift锚点。拖选开始、滚轮、失去捕获、取消或失活会清掉待处理动作；提交前后按记录ID核对排队刷新的列表。原有Ctrl/Shift、多选收拢、右键、行内按钮和方向键路径不变。

Release构建和自包含发布0警告0错误。自动回归通过：交互156项（新增44项，包含延迟保存true/false重载）、拖选43项（新增5项，包含实际WPF控件捕获下的滚轮和丢失捕获取消）、焦点54项、外观596项、UI性能50项、布局773项。Computer Use在独立合成数据窗口实际验证开关开启、第一次选中/第二次取消、取消后方向键继续选择、从唯一已选行拖成3条、多选收拢为1条后再次取消；没有操作日常历史。UI观察依据动作完成后的截图，`repeat-click-ui-v1.0.10/focus-ui-events.json`是预览输入事件时刻日志，延迟取消可能尚未提交，不能单独当作动作结束后的选择数量。

升级前正常退出并备份至`before-1.0.10-20260909-102008`；覆盖安装前后23个数据文件逐个SHA256一致，安装exe/dll/ICO与发布产物一致。已重新启动安装目录中的1.0.10，输入初始化成功、进程保持响应；未替用户开启新偏好，旧行为保持至用户主动打开开关。

## 1.0.9 去除方向键选择的整行焦点矩形

此前1.0.8刻意保留了方向键导航时的整行蓝框；本轮按用户进一步反馈取消记录行的矩形焦点轮廓。RowKeyboardFocusVisual改为无描边的2DIP灰色左侧标记，仅供Ctrl+方向键等焦点与选择分离的情况使用；IsSelected为true时装饰完全隐藏。普通上下键和Shift范围选择只有选中底色。按钮Tab焦点提示及1.0.8的截图/设置焦点修复保留，不清空键盘焦点或选择来隐藏框。

自动回归通过：`focus-v1.0.9/focus-results.json`54项，包含实际WPF焦点、生产方向键处理器、普通/Shift/Ctrl导航和装饰模板；`presentation-v1.0.9/presentation-results.json`596项；`interaction-v1.0.9.json`112项；自包含发布程序的`ui-performance-v1.0.9.json`50项。Release构建和自包含发布均0警告、0错误。本轮桌面实测遇到Windows锁屏，Computer Use已停止操作，因此没有把真实鼠标按键复测记为完成。未重测真实剪贴板、跨应用粘贴或屏幕帧率，本轮未改动这些实现。

升级前正常退出日常实例并备份至`before-1.0.9-20260909-003211`。覆盖安装前后23个用户数据文件逐个SHA256与备份一致，安装exe/dll/ICO与1.0.9发布产物一致；已启动已安装的1.0.9，进程完成输入初始化并保持响应。公开源码单独以54文件白名单导出，排除个人历史、截图、测试结果和本机报告；图标只在导出副本移除EXIF/文本元数据，IDAT像素内容不变。该导出目录独立Release构建0警告0错误，公开源码ZIP在构建前生成，不含bin/obj。

## 1.0.8 图标光学尺寸与明确的焦点提示

四款512像素原图的底板有效边界为38…473，旧图标画布占比仅85.2%。运行时与安装ICO采用同一个36/36/440/440取景，保留2像素抗锯齿余量，底板占比约99%；原PNG不改，四款方案保持独立。运行时缓存冻结256像素图标，安装ICO改为16/20/24/32/40/48/64/96/128/256十帧，`generate-icons.ps1`可重复生成，`build.ps1`自动调用。静态快捷方式与托盘仍使用原有默认方案1，窗口/任务栏尊重用户选择。

截图后的蓝框来自FocusVisualStyle，不是历史选中底色。新增FocusCuePolicy，明确记录Tab/方向/Home/End/Page等导航意图；Win/Snapshot、鼠标/触控、失活和隐藏清除意图，不将普通修饰键或截图后程序恢复焦点视作键盘导航。焦点装饰通过Adorner.AdornedElement检查目标自己的意图与IsKeyboardFocused，旧装饰不再照亮已失焦控件；主窗与预览窗均启用，正常Tab导航仍可见。不调用WPF内部接口，不清空选择来隐藏焦点框。

设置作为实际焦点边界，打开后聚焦首个外观按钮；ShowShelf、历史刷新和窗口激活优先保留设置内焦点，迟到的焦点请求用内容身份/版本校验，失活时不抢焦点，关闭后恢复原控件。独立菜单/下拉弹层保持其焦点路由。

验证：`focus-v1.0.8/focus-results.json`的33项策略、实际WPF焦点与装饰模板检查通过。`ui-performance-v1.0.8.json`共50项通过，包含新增30项图标画布占比、透明圆角、四方案独立性、冻结缓存及ICO帧验证。其余通过：交互112项、布局773项、外观590项、拖选38项、粘贴安全39项、托盘结构60项。Computer Use另在独立合成数据窗口实测鼠标→方向键→模拟截图加入记录→鼠标开设置→Tab→Escape→鼠标接管，日志为`focus-ui-v1.0.8/focus-ui-events.json`；目视确认鼠标无框、键盘导航有框、设置焦点不落到背景。没有通过自动化执行真实Win+Shift+S，也未修改用户截图快捷键或系统图标缓存；截图返回覆盖为策略/状态模拟及新增记录检查。

Release和自包含发布成功，0警告0错误；发布产物自检另57项通过，原剪贴板恢复，跨应用自动输入显式跳过。升级前正常退出并备份至`before-1.0.8-20260909-001717`；覆盖安装前后22个数据文件逐个散列相同，安装exe/dll/ICO与发布产物一致，已重新打开1.0.8。

## 1.0.7 拖选的单一捕获归属

原实现把鼠标捕获给ListBox，同时保留自定义范围选择和30ms边缘滚动。WPF的[ListBox实现](https://raw.githubusercontent.com/dotnet/wpf/v8.0.30/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/ListBox.cs)在本体获得捕获时启动另一套自动滚动；原生导航可改写选择，与应用范围更新冲突。四个指针事件和捕获/释放现在统一位于HistoryBorder，坐标仍相对HistoryList；不覆写WPF内部，也不通过延迟或隐藏列表掩盖闪烁。

捕获失败或丢失时立即停止拖选，忽略后代控件自身的丢捕获事件。拖选滚轮转发现有像素滚动并关闭惯性；普通滚轮路径不变。范围替换仍为单次选择事务，同一端点不重复更新，新增历史仍等松手后刷新。按Apple Design的连续反馈原则保持一套直接操作反馈；界面外观继续采用既有Win11设计，圆角和数据格式不变。

验证：`drag-v1.0.7/drag-selection-results.json`的38项隔离回归通过，包含旧ListBox捕获会启动原生计时器的对照、新Border捕获不会启动、反向/Ctrl范围、同端点50次不重复通知、上下边缘与滚轮、释放/丢捕获、到达记录延后刷新和按钮/滚动条隔离。离屏测试没有实际按住鼠标，仅在捕获断言期间屏蔽WPF因真实按钮Released而同步产生的MouseMove；范围、释放等仍由应用处理器与框架路由检查，不能冒充持续真实输入。实际输入另通过Computer Use操作100条合成记录：三次向下/反向/到底边拖选分别得到7/6/8条，再点击立即变为1条；前后日志分别为`drag-ui-before-v1.0.7`和`drag-ui-after-v1.0.7`中的`drag-ui-events.json`。新版每次捕获均为RoundedClipBorder，释放后为空，未发生集合Reset。工具不提供边缘长按停留或视频帧率测量，因此不声称长按视觉复现或实测FPS。

同时通过：交互112项、UI性能20项、布局773项、外观590项、粘贴安全39项、托盘结构与处理器60项。`tray-popup-v1.0.7`组件复测被Windows拒绝前台激活（`host-activation-blocked`），故该项本轮未通过，不计入成功检查；本轮没有修改托盘实现，1.0.6的28项结果保留作为之前验证记录。

Release构建与自包含发布均成功，0警告0错误。发布产物的`self-test-v1.0.7.json`另57项通过，原剪贴板已恢复；跨应用自动输入显式跳过，本轮未修改粘贴行为。覆盖安装前后均确认14个用户数据文件与`before-1.0.7-20260908-234812`备份逐个散列一致；安装exe/dll与1.0.7发布产物一致。

## 1.0.6 粘贴目标与托盘

旧粘贴流程先Hide再激活目标，且把SendInput接受输入直接标为“已粘贴”；新流程不隐藏窗口，保留任务栏入口，先确认目标窗口身份与前台焦点，再提交Ctrl+V。增加前台事件跟踪并保留轮询后备，排除Windows11托盘弹层与桌面；校验窗口/PID/线程身份，准备期间窗口、剪贴板或修饰键变化时中止，重复粘贴互斥。成功文案只说明快捷键已发送，不能证明任意第三方应用接受内容。

托盘左键单击ShowShelf、右键应用级Fluent菜单，移除WinForms ContextMenuStrip及历史正文子菜单。菜单固定五条命令与两分隔线，样式沿用已有8/4/0资源，记录数量及长文本不会撑开菜单。列表滚动、虚拟化与窗口DWM轮廓未修改。交互参考：[Windows通知区域](https://learn.microsoft.com/en-us/windows/win32/uxguide/winenv-notification)、[WPF菜单位置](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/popup-placement-behavior)。

隔离回归：`paste-safety-v1.0.6.json` 39项、`tray-v1.0.6/tray-results.json` 60项、`interaction-v1.0.6.json` 112项、`ui-performance-v1.0.6.json` 20项、`layout-v1.0.6/layout-regression-results.json` 773项、`presentation-v1.0.6/presentation-results.json` 590项通过。通过Computer Use操作独立粘贴夹具，按钮粘贴确实进入另一个进程的编辑框，合成多行Unicode文本逐字一致，主窗仍可见于任务栏；不向用户自己的聊天、文档或浏览器发送测试内容。当前桌面工具不暴露系统托盘，菜单原生交互使用夹具按钮调用同一个生产点击处理器，不等同实际通知区域图标端到端认证。

实测发现直接SetForegroundWindow到ContextMenu的popup HWND会在WPF焦点切换中立即关闭菜单；现由独立、非任务栏、1DIP透明临时owner先取得前台，菜单打开时只使用WPF焦点。菜单关闭即释放owner，旧菜单的延迟Closed回调不能影响新菜单或已重新打开的主窗。`tray-popup-v1.0.6/tray-popup-results.json`的28项真实popup组件检查通过：稳定展开超过1秒、主窗仍隐藏、框架Click打开主窗、Escape框架路由关闭、重复右键及迟到Closed竞态、取消旧排队请求及资源释放。Computer Use另目视确认菜单长期保持展开、点击外部关闭；这类组件检查不冒充真实托盘图标鼠标输入测试。

最终Release及自包含发布成功，0警告0错误；自包含程序的`self-test-v1.0.6.json`另57项通过并恢复原剪贴板。该自动套件的跨应用自动输入项显式跳过，本次跨进程粘贴由上面的Computer Use独立接收框实测完成。所有UI夹具正常退出后均恢复原剪贴板。覆盖安装前已正常退出并备份；安装后启动前确认14个用户数据文件与备份逐个散列相同，安装exe/dll与1.0.6发布产物散列相同。

## 1.0.5 共享圆角资源

按已确认的Windows圆角层级集中为三个资源：ControlCorners=4、OverlayCorners=8、SquareCorners=0。搜索/列表、按钮、输入框、选中标签和缩略图底板统一4；菜单/设置/下拉浮层及临时提示统一8；连续行背景与行焦点框为0，由最外层列表裁切。缩略图底板改用保留Geometry的RoundedClipBorder，内容与底板共用轮廓。普通控件焦点框为4，输入框保留PART_ContentHost、文字选择和编辑行为。

与1.0.4源码包逐字节核对HistoryListBox、WheelScrollMotion、WindowAppearance、MainWindow.Commands和MainWindow.xaml.cs均未修改；保持滚动、选择、外层DWM及窗口尺寸策略。本轮重点验证浅深色、不同窗口大小、125/150/200%栅格、选中接缝、焦点与设置控件，不将静态截图当作显示帧率测量。

验证：`presentation-v1.0.5/presentation-results.json` 590项、`interaction-v1.0.5.json` 112项、`layout-v1.0.5/layout-regression-results.json` 773项及`ui-performance-v1.0.5.json` 20项全部通过；Release构建0警告、0错误。已人工核对独立合成数据的浅深色主界面、菜单/下拉框和设置截图。设置截图捕获完整客户端以避免嵌套视觉偏移。本轮没有重测真实桌面DWM轮廓或显示帧率；外层窗口与滚动核心实现保持不变。

自包含发布程序另通过`self-test-v1.0.5.json`的57项应用及剪贴板检查，测试前内容已恢复；跨应用自动粘贴显式跳过。正常退出旧版后建立升级前备份，覆盖安装后、启动前核对13个用户数据文件与备份散列全部一致，安装程序及程序集与1.0.5发布产物散列一致。

## 1.0.4 外缘、菜单与滚动

最外缘旧版存在DWM轮廓与应用12DIP圆角/1DIP边线叠加；改为普通不透明无边线的客户端，只有DWM决定窗口轮廓。按应用主题更新原生边框，最大化/还原及DPI改变时更新策略；保留非零GlassFrameThickness，不在逐次尺寸变更中重建Window Region。内部圆角继续复用原Geometry。依据：[DWM圆角规则](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners)。

选中数量使用搜索栏内的轻量标签，预留槽位避免输入区在选择时跳动。记录菜单保留ContextMenu/MenuItem键盘语义，替换旧式模板为8DIP圆角、4DIP行高亮圆角、34DIP最小行高、16DIP图标、细边和轻阴影，支持浅深色；使用实色浮层，未伪称原生Acrylic。设计依据：[微软菜单](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/menus)、[Windows几何](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry)。

已核对WPF8.0.30源码：原生ScrollViewer的wheel处理依据正负调用上下滚动，像素模式仍按一整个刻度跳动。新实现保留delta/120的实际比例，精细输入即时推进；离散滚轮用真实elapsed-time驱动的可中断临界阻尼响应，方向改变抛弃原方向剩余距离。仅在活动期间订阅Rendering，重复时间戳去重；通过公开IScrollInfo.SetVerticalOffset更新实际回收面板，不添加RenderTransform、强制UpdateLayout或逐帧命令队列。按键、点击、设置、失活、隐藏和数据更新时停止余下动作。遵守系统行数和减少动画设置，不修改系统显示或注册表设置。沿用Apple Design技能的即时反馈和可中断运动原则。原生参考：[ScrollViewer](https://github.com/dotnet/wpf/blob/v8.0.30/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/ScrollViewer.cs)、[VirtualizingStackPanel](https://github.com/dotnet/wpf/blob/v8.0.30/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/VirtualizingStackPanel.cs)。

验证：`presentation-v1.0.4/presentation-results.json` 49项、`interaction-v1.0.4.json` 112项、`layout-v1.0.4/layout-regression-results.json` 761项、`ui-performance-v1.0.4.json` 20项通过。真实示例窗口检查圆角、最大化/还原、右键菜单、方向键高亮、Escape保留选择和鼠标滚动；不操作用户数据。

自包含发布程序另通过`self-test-v1.0.4.json`的57项应用及真实剪贴板检查，恢复测试前剪贴板。本轮未重测跨应用自动粘贴，报告明确列为跳过。升级前正常退出并备份，安装后启动前核对11个用户数据文件与备份散列一致，程序版本1.0.4。

`display-timing-v1.0.4.json`记录本机唯一活动桌面显示器3840×2160、160Hz，以及DWM rateCompose约160.018Hz（6.2493ms）；WPF DWM路径并非无条件锁60Hz。`scroll-render-v1.0.4.json`的27项测试含60/120/144/160/240Hz时间步等价性、反向、边界、取消和真实生产滚动路径：1000条合成记录时612次去重回调、594次实际列表偏移变化（97.1%）、8个已实现容器、0次集合Reset；回调间隔中位6.41ms、P95 10.25ms。本结果是特定文本夹具的UI回调/偏移统计，不是PresentMon显示帧跟踪，不能宣称每帧实显160FPS；图像密集内容、其它负载和设备仍需实际体验验证。

## 1.0.3 Windows 交互与撤销

保持1.0.2窗口、圆角和像素滚动实现。选择采用普通点击改选、Ctrl切换、Shift范围和Ctrl+Shift追加；拖选仍使用单事务和增量列表。去掉拖选后的固定点击等待期，以本次拖选释放事件保护代替，不延迟下一次明确点击；原有跨应用粘贴的按键释放和焦点安全检查不变。

内容快捷键按输入源分流：搜索保留文字编辑，按钮Enter/Space由按钮处理，列表处理记录命令。实线键盘焦点框与选中底色分开，无障碍列表项提供标题。设置面板限制Tab循环，并在关闭时恢复合理焦点。

删除采用最多10批内存深快照，可通过Ctrl+Z、工具栏或上下文菜单撤销。图片清理同时保护撤销记录引用，Clear结束撤销并保持清空备份屏障。恢复不驱逐删除后新进入的记录，也不覆盖已有相同内容/ID。撤销不跨启动、不支持重做；达到记录上限时可能只恢复部分条目。

`artifacts/storage-undo-tests-1.0.3.json`：独立存储项目99项检查通过，包括同步/后台保存、混合批恢复、深快照、10批边界、重复与ID冲突、容量保护、图片生命周期、Clear屏障和失败重试。`artifacts/interaction-v1.0.3.json`的112项独立合成交互检查、`ui-performance-v1.0.3.json`的20项性能检查与`layout-v1.0.3/layout-regression-results.json`的613项布局检查全部通过。等待搜索结果期间切换焦点、选择、窗口或设置会取消原先待处理的内容命令，避免稍后误粘贴。

在独立示例窗口实际验证普通点击直接改选、重复单击保留选择、右键菜单及Escape返回、设置关闭恢复按钮焦点、按钮Enter重新打开设置而不触发粘贴。实际桌面验证不操作用户历史记录；删除与撤销使用合成数据测试。交互合成检查不等于全系统键盘/读屏合规认证。

发布后的自包含程序通过`artifacts/self-test-v1.0.3.json`中的57项应用及真实剪贴板检查，测试结束恢复原剪贴板；本轮未重测跨应用自动粘贴，报告明确列为跳过。

## 1.0.2 缩放、边框与选中底色

标题区、搜索与列表的左右边缘统一；主面板圆角12DIP，内部裁切随边框厚度向内缩进。去掉ListBox/ScrollViewer默认内边距和焦点虚线，选中底色填满行及相邻选中行接缝；滚动条覆盖显示而不占用背景槽。

用固定圆角Geometry在Arrange阶段更新尺寸，避免SizeChanged阶段创建新裁切对象。WindowChrome改用非零GlassFrameThickness和DWM合成，不再于每次尺寸改变时重设旧式窗口区域；保持不透明背景和硬件渲染。依据：[WPF 8.0.30 WindowChromeWorker源码](https://github.com/dotnet/wpf/blob/v8.0.30/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Shell/WindowChromeWorker.cs)、[微软DWM圆角说明](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners)。

`artifacts/layout-v1.0.2/layout-regression-results.json`记录布局、像素及48步缩放验证：数据集合未Reset，容器复用，圆角Geometry保持同引用，DWM启用时未建立旧式Window Region，并检查滚动条与删除按钮至少2DIP间距。另生成12张浅/深色、小/大窗口、125/150/200%栅格输出。输出比例测试不等同实际跨显示器DPI切换；离屏像素检查也不是桌面GPU帧时间测量，不单独证明所有设备上都完全无闪烁。另通过独立示例窗口实际拖动左边框和上边框检查尺寸响应。

## 1.0.1 性能优化

本机 Windows 测量，2026-09-08。只使用独立合成数据；未把日常剪贴板内容放入报告。以下是特定路径微基准，不是整机 FPS、冷启动时间或所有使用场景的倍数承诺。

| 测试路径 | 优化前 | 优化后 | 范围 |
| --- | ---: | ---: | --- |
| 240 次新增/置顶的调用线程耗时 | 4,005–4,510 ms | 12.7–15.8 ms | 100 条记录、每条 2 KB 文字、3 轮；最终内容/顺序/置顶一致 |
| 300 次拖选指针更新 | 305.3 ms | 2.2 ms | 同一 WPF 列表、相同选择范围序列；不含人工手势时间 |
| 拖选产生的选择变更通知 | 11,949 次 | 30 次 | 每个新端点批量提交一次，相同端点零重复更新 |

后台保存另需约 60.5–75.7 ms 完成最终刷写，不计入上表调用线程时间。退出会等待刷写；失败时不自动放弃未保存数据。突然断电或强制结束进程可能丢失最后尚未落盘的记录。

## 实现边界

- 列表保持同一个数据集合，按记录 ID 增删/移动，保留选中项及浏览位置；像素滚动仍启用虚拟化和容器复用。
- 搜索合并约 60 ms 内的连续输入，后台执行并取消旧结果；完整文字、拼音、首字母、模糊匹配保留。首次遇到很长的中文内容时，拼音转换仍需要计算，界面可继续输入、滚动和取消。超过短暂等待会显示“正在搜索…”。
- PNG 编解码、磁盘写入、缩略图和图片预览移出界面线程。剪贴板读取/提交仍留在正确的 Windows UI 线程。限制图片解码并发，取消已经滚出视图的缩略图请求。
- 缓存不再达到数量上限后全量清空；托盘菜单在打开时重建，主题与图标未变则复用。
- 保留原有粘贴目标恢复和按键安全等待，没有通过缩短安全等待换取表面速度。

## 验证与复现

- `artifacts/storage-baseline.json`、`storage-deferred.json`：3 轮存储基线与优化后结果。
- `artifacts/storage-tests.json`：42 项数据层检查，包括深快照、原子备份、清空屏障、失败重试、文件保护和异步刷写期间的新记录。
- `artifacts/ui-performance.json`：合成 1000 条记录的 WPF 测试及拖选基准，检查虚拟化、位置/选中保留、快速搜索竞态等。
- `artifacts/search-baseline.json`、`search-optimized.json`、`search-regression-results.json`：搜索基线与优化结果，16,033项合成回归通过。1000条热搜索中，正文0.82ms、拼音23.29ms、首字母0.36ms；原方案因缓存反复清空，同组查询约14秒。首次大规模中文拼音建立仍有计算成本，不等同热缓存结果。
- `artifacts/self-test-v1.0.1.json`：56项应用/剪贴板检查通过，包括后台图片竞态、PNG透明度、文件、截图、快捷键及恢复原剪贴板；跨应用自动粘贴单独跳过。尝试交互测试时，编辑器前台焦点未能稳定保持，未把这项标为通过。原有粘贴安全逻辑及等待未改。
- `benchmarks/StorageBenchmark/README.md`：独立存储基准复现说明。
- `benchmarks/SearchBenchmark/README.md`：独立搜索基准和回归复现说明。
- `ClipShelf.exe --ui-performance-test <报告路径>`：重复 UI 检查，不读取剪贴板。
- `ClipShelf.exe --self-test --test-report <报告路径>`：真实剪贴板及跨进程粘贴检查。先退出日常实例，使用已解锁桌面；测试结束恢复原剪贴板。
- `ClipShelf.exe --render-qa <目录>`：用示例数据检查浅色、深色和设置面板。

界面实现参考了 Apple Design 技能的即时反馈、连续操作和减少无效重绘原则；没有增加会阻塞操作的装饰动画。
