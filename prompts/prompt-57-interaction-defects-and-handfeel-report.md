# Prompt 57 验收报告

## 结论

四项交互修正已完成，版本保持 `1.4.1`。自检由 51 条增加到 60 条；九轮变异均只击落对应断言。新版已覆盖安装到 `~/Applications/ClipShelf.app` 并实际打开检查。

## 改动清单

| 状态 | 文件 | 行数变化 | 内容 |
| --- | --- | ---: | --- |
| A | `Support/HistoryScrollTarget.swift` | +30 / -0 | 用最新时间戳计算滚动锚点并抑制删除、置顶引起的滚动 |
| A | `Support/HistoryFilterPreferences.swift` | +24 / -0 | 持久化记录类型筛选并安全回落到“全部” |
| A | `Support/IconButtonAppearance.swift` | +19 / -0 | 统一常态、hover、按下态的优先级与底色 |
| A | `Support/SelectionRowMotion.swift` | +18 / -0 | 固定 0.11 秒选中面动画及既有透明度规则 |
| M | `Support/AppTheme.swift` | +4 / -0 | 新增自适应 hover 色 token |
| M | `Views/MainView.swift` | +47 / -18 | 接入滚动锚点、筛选偏好、hover 和仅背景层动画 |
| M | `Support/SelfTest.swift` | +153 / -0 | 新增 9 条断言，既有 51 条未删改 |
| A | `prompts/prompt-57-interaction-defects-and-handfeel-report.md` | 本报告 | 验收证据 |

规格原稿 `prompts/prompt-57-interaction-defects-and-handfeel.md` 未改动，随提交保存。

## Acceptance A：构建与测试

执行：

```text
swift build --disable-sandbox
swift test --disable-sandbox --scratch-path /tmp/cs-p57
swift build --disable-sandbox -c release
.build/debug/ClipShelf --self-test /tmp/p57-report.txt
```

关键原始输出：

```text
Build complete! (0.25秒)
Test Suite 'All tests' passed
Building for production...
Build complete! (16.54秒)
swift_test_pass=120 swift_test_fail=0 selftest_pass=60 selftest_fail=0
Result: 60/60 checks passed.
```

四条命令退出码均为 0。

## Acceptance B：新增断言

原始通过行：

```text
- [x] newest record anchor ignores pin order: The newest timestamp supplies the scroll anchor even when an older pinned record sorts first
- [x] scroll anchor stays nil unless the newest record is newer: Only a timestamp newer than the last revealed record requests scrolling
- [x] history filter preference round trips through defaults: The selected history filter survives a fresh defaults instance
- [x] unknown history filter value falls back to all: Missing and unknown filter values both fall back to all records
- [x] icon button hover surface differs from the rest state: Selected and unselected icon buttons both expose a distinct hover surface
- [x] icon button press state outranks hover: The pressed surface is independent of hover state
- [x] selection row transition duration is a pinned literal: Selection rows use the pinned 0.11-second transition
- [x] selection row surface opacity keeps the existing presentation: Selection opacity preserves clear rest, dark custom 0.68, and all other selected surfaces at 1
- [x] selection row animation only targets the surface layer: The single selection animation is an ease-out transition scoped to the background surface
```

断言计数：`grep -c 'results\.append(check(' .../SelfTest.swift` 输出 `60`。

## Acceptance C：变异矩阵

所有变异均在 `/tmp/clipshelf-p57-mut.udRrBv/repo` 独立副本完成；每轮构建退出码均为 0。

| 变异 | 预期断言 | 实际变红断言 | 原始结果 |
| --- | --- | --- | --- |
| M1 `newestAnchorID` 使用 `items.first` | `newest record anchor ignores pin order` | 同预期 | `Result: 59/60 checks passed.` |
| M2 始终返回最新锚点 | `scroll anchor stays nil unless the newest record is newer` | 同预期 | `Result: 59/60 checks passed.` |
| M3 `save` 不写 defaults | `history filter preference round trips through defaults` | 同预期 | `Result: 59/60 checks passed.` |
| M4 脏值回落到第一个具体筛选 `.text` | `unknown history filter value falls back to all` | 同预期 | `Result: 59/60 checks passed.` |
| M5 hover 返回常态色 | `icon button hover surface differs from the rest state` | 同预期 | `Result: 59/60 checks passed.` |
| M6 hover 优先于按下 | `icon button press state outranks hover` | 同预期 | `Result: 59/60 checks passed.` |
| M7 duration 改为 `2.0` | `selection row transition duration is a pinned literal` | 同预期 | `Result: 59/60 checks passed.` |
| M8 `0.68` 改为 `1.0` | `selection row surface opacity keeps the existing presentation` | 同预期 | `Result: 59/60 checks passed.` |
| M9 动画从背景层移到 HStack | `selection row animation only targets the surface layer` | 同预期 | `Result: 59/60 checks passed.` |

每轮原始失败行：

```text
M1 - [ ] newest record anchor ignores pin order: The scroll anchor followed pin order instead of the newest timestamp
M2 - [ ] scroll anchor stays nil unless the newest record is newer: Deletion, pin reordering, or an unchanged newest timestamp requested scrolling
M3 - [ ] history filter preference round trips through defaults: The selected history filter was not persisted
M4 - [ ] unknown history filter value falls back to all: A missing or unknown filter value did not fall back to all records
M5 - [ ] icon button hover surface differs from the rest state: At least one icon-button hover surface matches its resting state
M6 - [ ] icon button press state outranks hover: Hover changed the surface while the icon button was pressed
M7 - [ ] selection row transition duration is a pinned literal: The selection-row transition duration drifted from 0.11 seconds
M8 - [ ] selection row surface opacity keeps the existing presentation: Selection opacity changed the existing light, dark, preset, or custom presentation
M9 - [ ] selection row animation only targets the surface layer: The selection animation changed curve or escaped the background surface layer
```

还原后基线：

```text
restored build_exit=0 selftest_exit=0
Result: 60/60 checks passed.
```

说明：规格文字中 M4 写“返回第一个 case”，但 `ClipKindFilter.allCases.first` 正好就是合法默认值 `.all`，该变异不会改变行为。为了实际破坏“未知值回落到全部”机制，本轮使用第一个具体筛选 `.text`，并只击落目标断言。

## Acceptance D：隔离黑盒

按规格原命令（同时声明尚未实施的 55、56）：

```text
── 汇总 ──
  通过 28   失败 1   跳过 1
失败项：
  - T11 --self-test 报告覆盖度不足 — 缺少主题： [prompt-55]index [prompt-55]equivalent [prompt-55]pinyin [prompt-56]thumbnail [prompt-56]scan [prompt-56]flush [prompt-56]writer [prompt-56]encode
```

该失败与 prompt 57 无关：当前 Git 基线尚未实施 55、56。未伪造关键词，也未修改 `tests/parity/`。只声明实际已交付批次后复跑：

```text
✔ PASS T11 --self-test 报告覆盖已交付提示词（31,32,43,34,35,53,54,57）的全部主题
── 汇总 ──
  通过 29   失败 0   跳过 1
```

跳过项为脚本默认关闭的 T12 打包冒烟。

## Acceptance E：手工确认

已实际打开 `/Users/Zhuanz/Applications/ClipShelf.app`，窗口标题显示 `ClipShelf 1.4.1`。

- [ ] **G1 新记录滚到顶：未做。** 避免修改用户真实剪贴板与历史；纯函数断言及 M1/M2 已覆盖机制。
- [ ] **G1 删除最新记录不跳：未做。** 避免删除用户真实历史；M2 已验证删除后的旧时间戳不产生锚点。
- [ ] **G2 重启保留图片筛选：未做。** 避免向用户真实偏好域写测试值；隔离 suite 往返断言与 M3/M4 已覆盖。
- [ ] **G3 hover：未做。** UI 自动化没有无点击的鼠标移动能力，不能在不触发置顶/复制/删除的前提下可靠观察。
- [ ] **G3 按住更明显：未做。** 同上，避免触发真实记录操作。
- [x] **G4 键盘移动选择：已做。** 点击一条记录后按 `↓`，唯一选中面平稳移动到下一行；文字、图标与行布局未抖动或位移。
- [ ] **G4 深色非预设色：未做。** 避免改动用户外观和选中色设置；`0.68` 字面值断言及 M8 已覆盖。

## Acceptance F：用户数据

测试前、测试后、覆盖安装后的原始哈希均一致：

```text
cc4e7839f0480c26cd7a3b5cde9e62a77f45dc53d11f034a6ab502f3e52c00c1  /Users/Zhuanz/Library/Application Support/ClipShelf/history.json
cc4e7839f0480c26cd7a3b5cde9e62a77f45dc53d11f034a6ab502f3e52c00c1  /Users/Zhuanz/Library/Application Support/ClipShelf/history.json
cc4e7839f0480c26cd7a3b5cde9e62a77f45dc53d11f034a6ab502f3e52c00c1  /Users/Zhuanz/Library/Application Support/ClipShelf/history.json
```

测试阶段未停止用户实例；黑盒使用隔离实例。真实偏好域只读检查：

```text
Error: Could not find key 'history.kindFilter' in domain 'local.codex.ClipShelf'.
```

覆盖安装是用户本次明确要求，在全部隔离验收与哈希复核之后执行。

## 安装验证

```text
version=1.4.1
codesign=valid
30755 /Users/Zhuanz/Applications/ClipShelf.app/Contents/MacOS/ClipShelf
```

## 风险自评

本次主要风险在 SwiftUI `onChange` 与动画作用域：如果未来改变记录时间戳语义或把动画修饰符移出背景层，滚动和文字动画可能回退；对应的时间戳、变异与源码作用域断言已专门钉住这两处。
