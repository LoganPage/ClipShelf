# prompt-50 实施报告：右键菜单镜像常量收口

## 完成状态

已完成。真实 `ClipRow` 右键菜单现在遍历 `ClipRowMenu.orderedActions` 生成，不再维护第二份手写菜单清单；菜单标题也收敛到纯函数。断言总数保持 **44**。

## 改动文件

```text
18  0  Sources/ClipShelfLite/Support/ClearHistoryConfirmation.swift
14  4  Sources/ClipShelfLite/Support/SelfTest.swift
19 11  Sources/ClipShelfLite/Views/MainView.swift
```

此外新增本报告文件。未修改 `Package.swift`、`tests/parity/`、`docs/`、`README.md`、`Assets/` 或 `script/`。

## A：构建与测试

### `swift build --disable-sandbox`

```text
Building for debugging...
[2/5] ClipShelf-product
[6/9] ClipShelf-product
[7/9] ClipShelf-product
[9/11] ClipShelf-product
Build complete! (3.77秒)
```

### `swift test --disable-sandbox --scratch-path /tmp/cs-p50`

最终原始关键输出：

```text
Build complete! (7.98秒)
[PASS] record context menu exposes the declared actions in order: The installed native row menu derives copy, paste, pin, and delete from the declared order
[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
Test Suite 'All tests' passed at 2026-09-30 18:19:45.909.
Executed 0 tests, with 0 failures (0 unexpected) in 0.000 (0.001) seconds
◇ Test run started.
↳ Testing Library Version: 2084
↳ Target Platform: arm64e-apple-macos14.0
✔ Test run with 0 tests in 0 suites passed after 0.001 seconds.
```

## B：反向验证

### 第 1 步：正常基线

```text
baseline exit=0
Result: 44/44 checks passed.
- [x] record context menu exposes the declared actions in order: The installed native row menu derives copy, paste, pin, and delete from the declared order
```

### 第 2 步：临时删除真实菜单并重建

临时从 `ClipRow` 删除整个 `.contextMenu { ... }` 修饰器，其余代码不动。构建原始结果：

```text
final mutation build exit=0
Build complete! (3.79秒)
```

自检原始结果：

```text
final mutation self-test exit=1
Result: 43/44 checks passed.
- [ ] record context menu exposes the declared actions in order: The record context menu is missing, detached from its declaration, or has divergent titles
```

失败列表只有上述一条目标断言。

### 第 3 步：完整恢复

使用补丁把真实菜单块原样恢复，再次构建和自检：

```text
restored exit=0
Result: 44/44 checks passed.
- [x] record context menu exposes the declared actions in order: The installed native row menu derives copy, paste, pin, and delete from the declared order
```

### 第 4 步：恢复证据

最终 `MainView.swift` 中的真实菜单：

```text
1095:        .contextMenu {
1096-            ForEach(ClipRowMenu.orderedActions, id: \.self) { action in
1097-                switch action {
1098-                case .copy:
1099-                    Button(ClipRowMenu.title(for: action)) {
1100-                        handleCopy()
1102-                case .paste:
1103-                    Button(ClipRowMenu.title(for: action)) {
1104-                        handlePaste()
1106-                case .pin:
1107-                    Button(ClipRowMenu.pinTitle(isPinned: item.isPinned)) {
1108-                        store.togglePinned(item)
1110-                case .delete:
1111-                    Button(ClipRowMenu.title(for: action), role: .destructive) {
1112-                        store.remove(item)
```

最终 `git diff` 只显示从四个手写按钮改为上述遍历，没有残留临时删除。

## C：断言回归

最终实现连续三次：

```text
final-repeat-1 exit=0
Result: 44/44 checks passed.
final-repeat-2 exit=0
Result: 44/44 checks passed.
final-repeat-3 exit=0
Result: 44/44 checks passed.
```

`SelfTest.swift` 只改写原右键菜单断言，没有新增或删除断言；其它 43 条保持原样。新判据覆盖：

- 动作顺序 `.copy → .paste → .pin → .delete`
- 复制、粘贴、删除三个固定标题
- 置顶与取消置顶标题
- `title(for: .pin)` 与未置顶标题来自同一来源
- 开发/对齐环境中 `MainView.swift` 真实菜单确实遍历 `orderedActions`

源码完整性检查只在源码文件可用时执行，避免分发后的 `.app` 因不携带 Swift 源码而让 `--self-test` 永久失败；动作与标题断言在任何构建里都强制执行。

## D：隔离对齐回归

执行命令：

```text
CLIPSHELF_PARITY_ISOLATED=1 CLIPSHELF_PARITY_DELIVERED=31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49 tests/parity/run.sh
```

关键原始输出：

```text
✔ PASS T11 --self-test 退出码 0，报告已生成
✔ PASS T11 --self-test 报告覆盖已交付提示词（31,32,43,33,38,37,34,35,40,41,36,42,44,45,46,47,49）的全部主题
Result: 44/44 checks passed.
✔ PASS T16 上限 10000 时进程未崩溃
✔ PASS T16 上限 10000 时 8 条全部累积
── 汇总 ──
通过 29   失败 0   跳过 1
```

跳过项只有脚本默认关闭的 T12 打包冒烟；本轮为 UI 验证已另行成功构建 App bundle。用户真实 `history.json` 未被修改。

## E：真实 UI

使用隔离数据目录启动最新 `dist/ClipShelf.app`，在一条临时文本记录上右键，实际菜单树：

```text
28 menu
    29 复制
    30 粘贴
    31 置顶
    32 删除
```

截图核对菜单与改动前一致：四项、顺序和原生样式均未变化。源码核对确认：

- 复制仍调用 `handleCopy()`。
- 粘贴仍调用 `handlePaste()`。
- 置顶仍调用 `store.togglePinned(item)`，只作用当前行。
- 删除仍调用 `store.remove(item)`，只作用当前行，并保留 `role: .destructive`。

## 为什么断言不再是影子

改动前，真实菜单和 `orderedActions` 是两份互不相干的清单，测试常量无法保护 UI。改动后：

1. `ClipRow` 直接遍历 `ClipRowMenu.orderedActions`，动作增删与顺序只有一个来源。
2. 固定标题由 `ClipRowMenu.title(for:)` 统一提供，置顶动态标题继续由 `pinTitle` 提供。
3. 纯函数 `isDeclaredMenuInstalled(in:)` 检查真实菜单源码确实存在 `.contextMenu` 且遍历声明清单。
4. 反向删除真实菜单后，目标断言实测由通过变为失败，证明引用边能被验收捕获。

因此这条断言现在同时保护声明内容和开发源码中的真实挂载，不再只是常量与同一字面量自比较。
