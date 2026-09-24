# Windows 1.3.5 测试对齐与验证

此轮只更正测试预期、测试结论与安装验证的取样时机，不改应用界面、尺寸、交互、历史格式或依赖。**无用户可见行为变化**，继续使用 1.3.5 版本号。首次动手前重新执行并读取了四组失败报告；对齐中发现的两处后续旧断言也单独核对。

| 失败项 | 性质 | 旧值 → 当前检查及依据 | 修复后实测 |
| --- | --- | --- | --- |
| 布局：滑块与行按钮间距 | 测试过期 | 旧检查要求整个 `Thumb` 热区相隔 ≥2 DIP；`App.xaml` 的可见 `Grip` 宽 5 DIP，而 `MainWindow.xaml` 的 13 DIP 滚动条扩大了透明拖拽热区。现检查热区不重叠、可见握柄相隔 ≥2 DIP，且热区至少比握柄宽 4 DIP。 | 浅/深色、两种窗宽、三档渲染缩放均通过；热区间距 0 DIP，可见间距 3 DIP；布局 773/773。 |
| 呈现：所有按钮必须 4 DIP 圆角 | 测试过期 | `App.xaml` 的普通 `ControlCorners` 为 4 DIP，但 `CaptionButton` 模板是原生风格方角；现按样式继承分别检查标题栏 0 DIP、内容按钮 4 DIP，并继续检查焦点样式。 | 呈现 572/572。 |
| 交互：寻找“图标方案 1/2”按钮 | 测试过期 | 设置页已经移除图标切换入口；`ThemeManager.Apply` 固定方案 2。现确认设置中不存在旧按钮、窗口图标确为方案 2，且主题切换不重建设置树。 | 交互 159/159。 |
| 图标：四款图案互不相同 | 测试过期 | `ThemeManager.Icon` 统一返回方案 2；现对旧选择值检查同一缓存对象及像素，同时保留尺寸、圆角透明区、系统 ICO 十档尺寸检查。 | UI 性能 62/62。 |
| UI 性能：主列表和设置滚动条外边距相同 | 测试过期 | 两处轨道均宽 13 DIP、共用握柄样式；`MainWindow.xaml` 外边距为 `0,3,0,3`，`App.xaml` 设置滚动条为 `2,3,0,3`，用于各自容器定位。现分别核对边距，并继续核对握柄大小与状态颜色。 | 主/设置握柄同宽，UI 性能 62/62。 |
| 托盘：切换主题后立即比较菜单颜色 | 测试过期 | `ThemeTransition` 对 `MenuSurfaceBrush` 使用 200 ms 颜色过渡；旧测试偶尔在首帧取色。现等过渡结束再比较浅/深色，同时要求动画确实结束。 | 托盘 62/62，浅/深色不同。 |

测试程序结论也已对齐：报告为 `passed: false`、缺失或损坏时，进程返回非零；缺失或损坏报告会写入失败摘要。受控探针验证三种情况均得到 `exit=1`、`passed=false`。覆盖安装脚本改为在应用正常退出并保存窗口状态后采集历史与设置摘要，因此只衡量安装过程本身。用户窗口宽度可能在正常退出时落盘，不能误算为安装篡改。

## 最终全套测试

报告根目录：`artifacts/v1.3.5-alignment-final2/`。每组 `passed=true`、进程退出码 0；共 **25 组、2,851 项检查**。

| 测试组 | 通过项 | 测试组 | 通过项 | 测试组 | 通过项 |
| --- | ---: | --- | ---: | --- | ---: |
| native-preview | 94 | text-preview | 66 | preview-interaction | 94 |
| common-preview | 98 | file-record | 23 | file-preview | 98 |
| copy-only | 29 | cleanup | 16 | tooltip | 32 |
| update | 30 | theme-transition | 17 | settings | 272 |
| drag | 49 | focus | 54 | tray-popup | 28 |
| layout | 773 | tray | 62 | presentation | 572 |
| ui-performance | 62 | interaction | 159 | scroll-render | 41 |
| multi-scroll | 42 | fine-scroll | 41 | preview-scroll | 41 |
| self-test | 58 | | | | |

`build.ps1 -SelfContained` 发布构建 0 警告、0 错误。`scripts/verify-install.ps1` 覆盖安装验证通过，程序版本 1.3.5，结果在 `artifacts/v1.3.5-aligned-install-final.json`，备份位于 `%LOCALAPPDATA%\ClipShelf-backups\update-20260923-222537-910e565f`。本地 ZIP 与 SHA-256 见 [1.3.5 发布候选记录](windows-v1.3.5.md)。未推送 GitHub、未删除历史备份。
