# prompt-31 交付报告：剪贴板敏感内容排除与测试底座

## 结果

- `org.nspasteboard.ConcealedType` 与 `org.nspasteboard.TransientType` 会在写入历史前被排除。
- 排除前会记录新的 pasteboard `changeCount`，同一次敏感复制不会被反复处理。
- 类型信息无法读取时按普通内容处理，避免异常丢失剪贴板内容。
- `CLIPSHELF_DATA_DIR` 指定 `history.json` 所在目录；未设置时仍使用 `~/Library/Application Support/ClipShelf`。
- `CLIPSHELF_DEFAULTS_SUITE` 指定应用设置域；未设置时仍使用标准设置域。
- 新增 SwiftPM test target 和 `ClipShelf --self-test <报告路径>`，两者复用 `ClipShelfSelfTest.run()` 中的同一组断言。
- `--self-test` 在 SwiftUI/AppKit 应用启动前执行，不创建窗口、不读取或写入系统剪贴板。

## 改动文件

- `Package.swift`
- `README.md`
- `Sources/ClipShelfLite/App/ClipShelfLiteApp.swift`
- `Sources/ClipShelfLite/main.swift`
- `Sources/ClipShelfLite/Stores/ClipStore.swift`
- `Sources/ClipShelfLite/Support/AppEnvironment.swift`
- `Sources/ClipShelfLite/Support/ClipboardHistoryPolicy.swift`
- `Sources/ClipShelfLite/Support/SelfTest.swift`
- `Sources/ClipShelfLite/Models/HotKeyConfiguration.swift`
- `Sources/ClipShelfLite/Services/ScreenshotFolderWatcher.swift`
- `Sources/ClipShelfLite/Support/AppIconPreferences.swift`
- `Sources/ClipShelfLite/Support/AppearancePreferences.swift`
- `Sources/ClipShelfLite/Support/SelectionClickBehaviorPreferences.swift`
- `Sources/ClipShelfLite/Support/SelectionColorPreferences.swift`
- `MacTests/ClipShelfLiteTests/SelfTestTests.m`

## 自动化验证

### `swift build`

结果：通过，退出码 `0`。

```text
Building for debugging...
Build complete!
```

### `swift test`

结果：通过，退出码 `0`。当前机器只有 Command Line Tools，测试 target 使用加载桥接调用共享 Swift 断言。

```text
[PASS] concealed content is excluded
[PASS] excluded content advances change count
[PASS] transient content is excluded
[PASS] ordinary content is included
[PASS] unknown type information is included
[PASS] data directory isolation
[PASS] defaults suite isolation
```

### `--self-test`

执行命令：

```bash
.build/debug/ClipShelf --self-test /tmp/clipshelf-prompt31-isolation.md
```

结果：通过，退出码 `0`，报告为 `7/7 checks passed`。执行前后真实历史文件 SHA-256 均为：

```text
028c589441399635a31b68015dd9041b06ad24f4b2cc37f9b39aa8bcc9cf11da
```

## 手工验证步骤

1. 在隔离数据目录和设置域启动 Debug 版 ClipShelf。
2. 使用独立脚本向 pasteboard 写入普通文本以及 `org.nspasteboard.ConcealedType` 标记，等待两秒。
3. 确认隔离目录下的 `history.json` 没有新增敏感记录。
4. 对 `org.nspasteboard.TransientType` 重复步骤 2–3。
5. 写入不带标记的文本、文件和图片，确认原有采集、优先级和去重行为不变。
6. 连续等待多个轮询周期，确认被排除的同一次复制不会被重复处理。

本轮未执行 Release 构建、覆盖安装、提交、推送或 GitHub 发布。
