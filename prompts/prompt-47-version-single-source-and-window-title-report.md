# prompt-47 实施报告：版本单一来源与窗口标题

## 完成状态

已完成。应用版本设为 **1.4.1**，`CFBundleShortVersionString` 在 `Sources/` 中只剩一个读取点。打包应用标题显示 `ClipShelf 1.4.1`，SwiftPM 裸二进制标题回退为 `ClipShelf`，控制接口在裸二进制下返回 `dev`。

## 改动文件

```text
1   1  Sources/ClipShelfLite/App/AppDelegate.swift
1   2  Sources/ClipShelfLite/Services/AppUpdateChecker.swift
1   2  Sources/ClipShelfLite/Services/RuntimeControlServer.swift
20  0  Sources/ClipShelfLite/Support/AppVersionInfo.swift
11  0  Sources/ClipShelfLite/Support/SelfTest.swift
1   1  Sources/ClipShelfLite/Views/MainView.swift
1   1  script/build_app_bundle.sh
```

`MainView.swift` 的一行兼容修改把主窗口标题判定由完全相等改为 `hasPrefix("ClipShelf")`。原因是标题加入版本后，旧判定会让现有键盘事件全部失效；该修改没有改变任何按键、选择或拖选规则。

## A：构建与测试

### `swift build --disable-sandbox`

```text
Building for debugging...
[Planning deferred tasks]
[5/10] ClipShelf-product
[12/17] ClipShelf-product
[13/17] ClipShelf-product
[15/17] ClipShelf-product
Build complete! (4.22秒)
```

### `swift test --disable-sandbox --scratch-path /tmp/cs-batch-e`

```text
Build complete! (16.96秒)
[PASS] app version has one display source and safe development fallbacks: Window title and status version share the bundle version while unbundled builds stay identifiable
[PASS] self test defaults suite file count does not grow: The isolated self-test defaults suite does not increase files in Library/Preferences
Test Suite 'All tests' passed at 2026-09-30 16:48:17.322.
Executed 0 tests, with 0 failures (0 unexpected) in 0.000 (0.002) seconds
◇ Test run started.
↳ Testing Library Version: 2084
↳ Target Platform: arm64e-apple-macos14.0
✔ Test run with 0 tests in 0 suites passed after 0.001 seconds.
```

## B：打包标题

`./script/build_app_bundle.sh` 原始关键输出：

```text
Build complete! (0.27秒)
/var/folders/71/z1mzqlr977dbzjhpzy3jd1nw0000gp/T//clipshelf-app-bundle/ClipShelf.app: replacing existing signature
/var/folders/71/z1mzqlr977dbzjhpzy3jd1nw0000gp/T//clipshelf-app-bundle/ClipShelf.app: valid on disk
/var/folders/71/z1mzqlr977dbzjhpzy3jd1nw0000gp/T//clipshelf-app-bundle/ClipShelf.app: satisfies its Designated Requirement
/var/folders/71/z1mzqlr977dbzjhpzy3jd1nw0000gp/T//clipshelf-app-bundle/ClipShelf.app
```

Info.plist 与签名：

```text
CFBundleShortVersionString = 1.4.1
codesign --verify --deep --strict dist/ClipShelf.app
valid
```

实际启动 `dist/ClipShelf.app` 后，只读界面检查原文：

```text
Window: "ClipShelf 1.4.1", App: ClipShelf.
0 standard window ClipShelf 1.4.1, Secondary Actions: Raise
```

原规格建议的 System Events 命令在本机被系统辅助功能权限拒绝，原始错误为：

```text
“System Events”遇到一个错误：“osascript”不允许辅助访问。 (-1719)
```

因此改用 Codex Computer Use 的只读应用状态取得上述真实窗口证据，没有修改系统权限。

## C：未打包标题

实际启动 `.build/debug/ClipShelf` 后，只读界面检查原文：

```text
Window: "ClipShelf", App: ClipShelf.
0 standard window ClipShelf, Secondary Actions: Raise
```

未打包状态接口返回 `dev`，不会再伪报旧版本 `1.2.0`。这不改变 `--ctl status` 的字段名或 JSON 结构。

## D：新增断言

新增 1 条聚合断言，同时覆盖：

- `windowTitle(bundleVersion: "1.4.1") == "ClipShelf 1.4.1"`
- `windowTitle(bundleVersion: nil) == "ClipShelf"`
- `statusVersion(bundleVersion: "1.4.1") == "1.4.1"`
- `statusVersion(bundleVersion: nil) == "dev"`

断言原始结果：

```text
[PASS] app version has one display source and safe development fallbacks: Window title and status version share the bundle version while unbundled builds stay identifiable
```

## E：回归与单一来源

连续三次自检：

```text
self-test-1 exit=0
Result: 42/42 checks passed.
self-test-2 exit=0
Result: 42/42 checks passed.
self-test-3 exit=0
Result: 42/42 checks passed.
```

单一读取点：

```text
$ rg -n "CFBundleShortVersionString" Sources/
Sources/ClipShelfLite/Support/AppVersionInfo.swift:5:        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
```

`tests/parity/runtime_control.sh` 原始汇总：

```text
── T-A 默认不开启端点 ──
✔ PASS T-A 未设 CLIPSHELF_CONTROL_SOCKET 时不创建控制 socket
✔ PASS T-A 未设 socket 时 --ctl 退出码 2 且输出可读 JSON
── T-B 控制通道基本能力 ──
✔ PASS T-B 隔离实例启动，控制 socket 已建立
✔ PASS T-B socket 权限为 0600
✔ PASS T-B ping 返回 ok
✔ PASS T-B status 初始记录数为 0
── T-C 与其它剪贴板隔离（关键） ──
✔ PASS T-C 向另一个具名剪贴板写入后，隔离实例仍为 0 条（只读自己的剪贴板）
── T-D 注入 / 导出 / 上限 / 清空 / 退出 ──
✔ PASS T-D 注入文本后实例记录数为 1
✔ PASS T-D export 导出内存记录成功（1 条）
✔ PASS T-D set-limit 5 生效
✔ PASS T-D clear 后记录数为 0
✔ PASS T-D quit 后进程正常退出
✔ PASS T-D 退出后 socket 已被移除
── T-E 真实数据保护 ──
✔ PASS T-E 用户真实 history.json 未被改动
✔ PASS T-E 历史上限写入了隔离偏好域
── 汇总 ──
通过 15   失败 0
```

## 版本假设

按规格将版本设为 **1.4.1**，唯一打包常量位于 `script/build_app_bundle.sh`。以后改变版本只需修改这一处；产品源码读取打包后的 Info.plist，不再保留硬编码发布版本。
