# Prompt 54 验收报告

## 结论

Prompt 54 已完成。产品行为未改动；源码扫描检查得到加固，旧历史清理的保留天数、默认值、选项集合与可撤销删除路径均有独立自检保护。自检断言由 47 条增至 51 条。

## 改动清单

| 状态 | 文件 | 行数变化 | 内容 |
| --- | --- | ---: | --- |
| A | `Sources/ClipShelfLite/Support/SourceScan.swift` | +124 / -0 | 新增源码词法清理、类型体/函数体提取与正则匹配工具 |
| M | `Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift` | +47 / -27 | 菜单检查限定到 `ClipRow`，要求恰好一个菜单并绑定删除角色 |
| M | `Sources/ClipShelfLite/Support/SelfTest.swift` | +205 / -1 | 加强既有保留期断言并新增 4 条断言 |
| A | `prompts/prompt-54-harden-source-scan-and-pin-cleanup-quantities-report.md` | 本报告 | 验收证据 |

规格原稿 `prompts/prompt-54-harden-source-scan-and-pin-cleanup-quantities.md` 未改动，随本次提交保存。

## Acceptance A：构建与测试

执行：

```text
swift build --disable-sandbox
swift test --disable-sandbox --scratch-path /tmp/cs-p54-final
.build/debug/ClipShelf --self-test /tmp/p54-final-report.txt
```

关键原始输出：

```text
Building for debugging...
Build complete! (3.51秒)

Test Suite 'All tests' passed
[PASS] ...（共享自检共输出两轮）
final_test_pass=102 final_test_fail=0 final_selftest_pass=51 final_selftest_fail=0

Result: 51/51 checks passed.
- [x] row menu source check accepts the installed menu and tolerates reflow
- [x] row menu source check rejects comments decoys and misplaced roles
- [x] old history cleanup cutoff matches the declared retention window exactly
- [x] old history cleanup routes through the shared removal path so it stays undoable
```

结果：构建退出码 0，`swift test` 为 102 PASS / 0 FAIL，独立自检为 51/51、退出码 0。

## Acceptance B：未变异基线

当前未变异的 `MainView.swift`、`ClipStore.swift` 与支持代码运行结果：

```text
Result: 51/51 checks passed.
```

四条新增断言在正常代码上均为绿色。

## Acceptance C：真实文件反向验证

每轮均先修改真实 `Sources/ClipShelfLite/Views/MainView.swift`，运行合法 Swift 构建，再运行自检；下一轮前完整还原。

```text
C1 注释整个真实 contextMenu
C1 build_exit=0 selftest_exit=1
Result: 50/51 checks passed.
- [ ] record context menu exposes the declared actions in order

C2 插入合规诱饵菜单，并移除真实删除项的 destructive role
C2 build_exit=0 selftest_exit=1
Result: 50/51 checks passed.
- [ ] record context menu exposes the declared actions in order

C3 把 destructive role 从 delete 移到 pin
C3 build_exit=0 selftest_exit=1
Result: 50/51 checks passed.
- [ ] record context menu exposes the declared actions in order
```

还原证明：

```text
mainview_restore_exit=0
restored_selftest_exit=0
51
MainView worktree SHA-256 = 43aaff296b579cf28fe109f6f31bcb83a710602cc0e98ad3c07feac76a9eb916
MainView HEAD SHA-256     = 43aaff296b579cf28fe109f6f31bcb83a710602cc0e98ad3c07feac76a9eb916
```

## Acceptance D：九轮变异矩阵

变异在 `/tmp/clipshelf-p54-mut.1oI5xI/repo` 独立副本内完成。每轮先恢复全部相关文件；九轮构建退出码均为 0。

```text
M1 codeOnly 直接 return source
build_exit=0 selftest_exit=1
Result: 50/51 checks passed.
- [ ] row menu source check rejects comments decoys and misplaced roles

M2 移除字符串字面量处理，保留注释处理
build_exit=0 selftest_exit=1
Result: 50/51 checks passed.
- [ ] row menu source check accepts the installed menu and tolerates reflow

M3 仅使用第一个菜单块
build_exit=0 selftest_exit=1
Result: 50/51 checks passed.
- [ ] row menu source check rejects comments decoys and misplaced roles

M4 destructive role 只要求块内存在
build_exit=0 selftest_exit=1
Result: 50/51 checks passed.
- [ ] row menu source check rejects comments decoys and misplaced roles

M5 移除 ForEach(ClipRowMenu.orderedActions) 引用边
build_exit=0 selftest_exit=1
Result: 49/51 checks passed.
- [ ] row menu source check accepts the installed menu and tolerates reflow
- [ ] row menu source check rejects comments decoys and misplaced roles

M6 86_400 秒改为 3_600 秒
build_exit=0 selftest_exit=1
Result: 50/51 checks passed.
- [ ] old history cleanup cutoff matches the declared retention window exactly

M7 默认保留期 30 改为 15
build_exit=0 selftest_exit=1
Result: 49/51 checks passed.
- [ ] old history cleanup retention window only accepts the offered values
- [ ] old history cleanup cutoff matches the declared retention window exactly

M8 绕过 remove(ids:) 直接 removeAll + save
build_exit=0 selftest_exit=1
Result: 50/51 checks passed.
- [ ] old history cleanup routes through the shared removal path so it stays undoable

M9 移除类型名尾部词边界
build_exit=0 selftest_exit=1
Result: 50/51 checks passed.
- [ ] row menu source check accepts the installed menu and tolerates reflow
```

九轮失败断言集合均与规格矩阵完全一致。

## Acceptance E：断言完整性

执行与原始输出：

```text
git show 8a8d31c:Sources/ClipShelfLite/Support/SelfTest.swift | grep -c 'results\.append(check('
47
grep -c 'results\.append(check(' Sources/ClipShelfLite/Support/SelfTest.swift
51

removed:
（无）

added:
old history cleanup cutoff matches the declared retention window exactly
old history cleanup routes through the shared removal path so it stays undoable
row menu source check accepts the installed menu and tolerates reflow
row menu source check rejects comments decoys and misplaced roles

protected names:
old history cleanup retention window only accepts the offered values
record context menu exposes the declared actions in order
```

既有断言名消失 0 条，新增 4 条，两个受保护名称保持逐字一致。

## Acceptance F：隔离黑盒回归

不带 54：

```text
✔ PASS T11 --self-test 报告覆盖已交付提示词（31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49）的全部主题
── 汇总 ──
  通过 29   失败 0   跳过 1
```

带 54：

```text
✔ PASS T11 --self-test 报告覆盖已交付提示词（31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49,54）的全部主题
── 汇总 ──
  通过 29   失败 0   跳过 1
```

两轮 T16 均一次通过。唯一跳过项均为按脚本默认设置跳过的 T12 打包冒烟；未修改 `tests/parity/`。

## Fail-open 说明

A48、A49、A50 是纯逻辑断言，不会 fail-open。A51 需要读取源码；在 `.app` 打包分发场景中沿用既有 `runningFromAppBundle` 口径，因此源码不可读时会 fail-open，等价于该断言在分发包中不存在。源码树测试、SwiftPM 测试与本报告的变异测试均实际执行该断言。

## 自评

新增 4 条断言都能测出明确缺陷，没有一条只是凑数；九轮独立变异已分别证明其保护范围。
