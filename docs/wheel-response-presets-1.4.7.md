# Windows 1.4.7 滚轮响应预设

本版增加首帧预热与三档滚轮响应预设，供真实鼠标滚轮对比。所有新增开关默认关闭；不加参数启动时仍保持 1.4.6 的滚动曲线。用户尚未选定档位，因此本版不修改默认手感，也不宣称滚动卡顿已经解决。

## 直接对比六种手感

每次测试前先完全退出托盘中的 ClipShelf，再在安装目录运行其中一条：

```powershell
0) .\ClipShelf.exe                                    # 基线（当前手感）
1) .\ClipShelf.exe --wheel=snappy                     # 刚度 90 + 预热 90 ms（最跟手）
2) .\ClipShelf.exe --wheel=balanced                   # 刚度 65 + 预热 60 ms（推荐）
3) .\ClipShelf.exe --wheel=soft                       # 刚度 45 + 预热 0 ms（略快于现状）
4) .\ClipShelf.exe --wheel=native                     # 完全交回系统原生
5) .\ClipShelf.exe --wheel=instant                    # 自研路径但不做动画
```

| # | 命令 | 跟手度（好 / 无变化 / 更差） | 一顿一顿（好 / 无变化 / 更差） | 是否生硬 | 备注 |
| --- | --- | --- | --- | --- | --- |
| 0 | 基线 | — | — | — | |
| 1 | `--wheel=snappy` | | | | |
| 2 | `--wheel=balanced` | | | | |
| 3 | `--wheel=soft` | | | | |
| 4 | `--wheel=native` | | | | |
| 5 | `--wheel=instant` | | | | |

也可以单独微调；显式数值优先于预设：

```powershell
.\ClipShelf.exe --wheel-prewarm=60
.\ClipShelf.exe --wheel-response=75 --wheel-prewarm=60
.\ClipShelf.exe --wheel=snappy --wheel-response=45 --wheel-prewarm=30
```

## 真实输入诊断

```powershell
.\ClipShelf.exe --scroll-diag "$env:USERPROFILE\Desktop\clipshelf-scroll-diag.json"
```

使用真实鼠标滚轮滚动，感觉卡时按 `Ctrl+Shift+F12` 保存最近 20 秒。诊断模式仍使用用户历史和设置的临时副本，不向 `%LOCALAPPDATA%\ClipShelf` 写入诊断期间的数据。

## 预测与实测对账

以下实测栏必须由真实滚轮报告填写；当前没有用户报告，不以合成输入冒充实测。

| 启动方式 | 预测 `settleMs` p50 | 实测 | 预测 `inputToFirstMoveMs` | 实测 | `compositionIntervalMs` max |
| --- | ---: | ---: | ---: | ---: | ---: |
| 基线 | 约 350 ms | 待测 | 0–20 ms | 待测 | 待测 |
| `--wheel=snappy` | ≤120 ms 目标 | 待测 | ≤16 ms 目标 | 待测 | 待测 |
| `--wheel=balanced` | 约 150–170 ms | 待测 | ≤16 ms 目标 | 待测 | 待测 |
| `--wheel=soft` | 介于基线与 balanced 之间 | 待测 | 待测 | 待测 | 待测 |
| `--wheel=native` | 0–30 ms | 待测 | 0–20 ms | 待测 | 待测 |
| `--wheel=instant` | 0–30 ms | 待测 | 0–20 ms | 待测 | 待测 |

## 实现与断言

- 预热发生在首个合成帧前，只推进当前位置，不改变最终目标。
- `--wheel=instant`、`--wheel=native`、Windows“关闭动画”和拖选直接输入均不会执行预热。
- `--wheel=snappy` 展开为响应 90、预热 90 ms；`balanced` 为 65/60；`soft` 为 45/0。
- 显式 `--wheel-response=` 与 `--wheel-prewarm=` 覆盖预设值。
- 60 ms 预热在响应 28 的隔离运动测试中首帧前推进 50.1%，落在 45%–55% 目标区间。
- 新增开关测试共 21 项，包含默认关闭、参数优先级、目标不变、边界裁剪和互斥路径。

## 尚待用户回答

1. 基线 `settleMs` 是否接近 350 ms；偏差是多少。
2. 是否出现超过 33.3 ms 的合成帧，以及发生在慢滚还是快滚。
3. 三档预设中哪一档最舒服。
4. 触控板手感是否保持不变。
5. 更跟手后是否觉得生硬。

收到用户填写的判定表和诊断报告后，才能决定是否以及如何修改默认响应曲线。

## 本地回归

| 入口 | 结果 |
| --- | --- |
| `--runtime-switch-test` | 21 / 0 |
| `--presentation-test` | 643 / 0 |
| `--type-filter-test` | 34 / 0 |
| `--interaction-test` | 163 / 0 |
| `--settings-test` | 296 / 0 |
| `--drag-test` | 49 / 0 |
| `--focus-test` | 54 / 0 |
| `--layout-test` | 785 / 16，仍为既有同名基线项，没有新增失败 |
| `--smoothness-regression-test` | 3 / 0 |

Release 构建、覆盖安装和用户数据校验结果在交付时记录；不会把 `bin/`、`obj/`、`artifacts/` 或 `dist/` 提交进 Git。
