# Windows 1.4.6 滚动诊断与单项开关

这版只增加被动诊断和八个彼此独立的定位开关。所有开关默认关闭，正常启动保留 1.4.5 的滚动、筛选蒙层和列表行为。尚未得到用户实机判定，因此本版不宣称滚动卡顿已修复，也没有猜测性改动默认滚动算法。

## 真实输入诊断

先完全退出托盘中的 ClipShelf，再在安装目录运行：

```powershell
.\ClipShelf.exe --scroll-diag "$env:USERPROFILE\Desktop\clipshelf-scroll-diag.json"
```

使用真实鼠标滚轮或触控板滚动，随后按 `Ctrl+Shift+F12`。程序会保存最近 20 秒的合成帧时钟、回调/合成帧比率，以及每个滚轮包的首次移动、95% 到达、停止和过冲数据，并在窗口内提示已保存。诊断模式从 `%LOCALAPPDATA%\ClipShelf` 复制历史和设置到临时目录；源数据只读，运行期间的新剪贴板记录只写临时副本。

报告包括：

- `compositionIntervalMs`：相邻不同 `RenderingTime` 的 p50 / p90 / p95 / p99 / max；
- `callbacksPerCompositionFrame`：渲染回调数除以不同合成帧数；若大于 1.5，报告明确提示旧的回调间隔不能当帧率；
- `inputToFirstMoveMs`、`timeToTarget95Ms`、`settleMs`、`overshootDip`：逐包明细和分布。

## 八个独立开关

每次先完全退出托盘中的 ClipShelf，只运行下面一条，滚动同一段记录，然后记录“跟手度”和“掉帧”两项感受：

```powershell
1) .\ClipShelf.exe --wheel=native            # 滚一下，回答：跟手 / 掉帧 各自 好 / 无变化
2) .\ClipShelf.exe --wheel=instant
3) .\ClipShelf.exe --wheel-response=60
4) .\ClipShelf.exe --wheel-pixel-snap
5) .\ClipShelf.exe --no-row-cache
6) .\ClipShelf.exe --thumbnail-quality=low
7) .\ClipShelf.exe --no-row-motion
8) .\ClipShelf.exe --no-drag-render
```

| # | 命令 | 跟手度（好 / 无变化 / 更差） | 掉帧（好 / 无变化 / 更差） | 备注 |
| --- | --- | --- | --- | --- |
| 0 | 直接启动（1.4.5 基线） | — | — | 先记录基线感受 |
| 1 | `--wheel=native` | | | |
| 2 | `--wheel=instant` | | | |
| 3 | `--wheel-response=60` | | | |
| 4 | `--wheel-pixel-snap` | | | |
| 5 | `--no-row-cache` | | | |
| 6 | `--thumbnail-quality=low` | | | |
| 7 | `--no-row-motion` | | | |
| 8 | `--no-drag-render` | | | |

收到这张表之前不进入产品修复阶段。若八项都无变化，将继续用同样方法隔离虚拟化回收、分隔线、文本渲染、缓存长度和外层阴影，而不是凭感觉修改。

## 探针修正

合成探针现在同时输出 `compositionIntervalMs` 与 `callbacksPerCompositionFrame`，并记录每个自定义滚轮包的响应阶段。合成夹具的图片行改为各自独立、完全不透明的渐变/噪点 PNG，不再共用一张全透明图片。真实历史探针仍只使用用户数据的副本。

本机构建后的单次小图夹具读数：`callbacksPerCompositionFrame = 2.0`；`compositionIntervalMs` p50 4.17 ms、p90 4.73 ms、p99 9.41 ms、max 14.73 ms。该比率已经超过 1.5，因此此前仅以回调间隔推断屏幕帧率的结论不可靠。这里仍不使用合成夹具数据代替用户实机判断。

## 本地回归

| 入口 | 结果 |
| --- | --- |
| `--runtime-switch-test` | 10 / 0 |
| `--presentation-test` | 643 / 0 |
| `--type-filter-test` | 34 / 0 |
| `--interaction-test` | 163 / 0 |
| `--settings-test` | 296 / 0 |
| `--drag-test` | 49 / 0 |
| `--focus-test` | 54 / 0 |
| `--layout-test` | 785 / 16，失败断言名与既有基线相同（12 个滚动条命中间距、4 个 200% 离屏窗口角像素） |
| `--smoothness-regression-test` | 3 / 0 |

Release 构建为 0 个警告、0 个错误。布局入口按项目既有规则会因 16 个已记录基线项返回非零；没有修改或放宽这些断言。
