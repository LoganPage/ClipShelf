# prompt-56 流畅度第二批交付报告

日期：2026-10-03
仓库：`/Users/Zhuanz/Documents/Codex/2026-05-16/macos-windows`

## 1. 改动清单

### G0 模糊匹配语义钉住

| 文件 | 增/删 | 内容 |
| --- | ---: | --- |
| `Sources/ClipShelfLite/Support/SearchMatcher.swift` | +1/-1 | `fuzzyContains` 仅由 `private` 改为模块内可见；算法和常量未改。 |
| `Sources/ClipShelfLite/Support/SelfTest.swift` | 包含于 +137/-0 | 新增 9 组字面值断言。 |

### G1 Release 打包

| 文件 | 增/删 | 内容 |
| --- | ---: | --- |
| `script/build_app_bundle.sh` | +2/-2 | 两处 `swift build` 均加入 `-c release`，包括 `--show-bin-path`。 |

### G2 截图文件夹低频兜底

| 文件 | 增/删 | 内容 |
| --- | ---: | --- |
| `Sources/ClipShelfLite/Services/ScreenshotFolderWatcher.swift` | +4/-2 | 保留文件系统事件源，将 500 ms 轮询改为具名常量 `minimumRescanInterval = 5` 秒。截图判定逻辑未改。 |

选择 5 秒兜底而非完全删除定时器：文件系统事件仍是主路径，同时保留低频容错，空闲扫描频率由 2 次/秒降为 0.2 次/秒。

### G3 后台缩略图缓存

| 文件 | 增/删 | 内容 |
| --- | ---: | --- |
| `Sources/ClipShelfLite/Support/ImageThumbnailCache.swift` | +143/-0 | 新增线程安全、有上限的 LRU 缓存；后台生成 104×80 px 缩略图。 |
| `Sources/ClipShelfLite/Views/MainView.swift` | +34/-15 | 图片行只读取缓存，缓存未命中时后台生成；损坏图片回落 `photo` 占位图。 |

### G4 TIFF 转 PNG 后台编码

| 文件 | 增/删 | 内容 |
| --- | ---: | --- |
| `Sources/ClipShelfLite/Support/ImageCaptureEncoder.swift` | +23/-0 | 新增纯 ImageIO TIFF→PNG 编码函数。 |
| `Sources/ClipShelfLite/Stores/ClipStore.swift` | 与 G5 合计 +37/-28 | 主线程只读取剪贴板变化与原始数据，TIFF 编码进入专用后台队列，完成后回主线程入库。 |

### G5 合并保存与退出 flush

| 文件 | 增/删 | 内容 |
| --- | ---: | --- |
| `Sources/ClipShelfLite/Support/HistoryWriter.swift` | +56/-0 | 250 ms 合并窗口、串行后台队列、原子写、同步幂等 `flush()`、可观测 `writeCount`。 |
| `Sources/ClipShelfLite/Stores/ClipStore.swift` | 与 G4 合计 +37/-28 | `save()` 改为调度最新快照，暴露退出 flush。 |
| `Sources/ClipShelfLite/App/AppDelegate.swift` | +1/-0 | `applicationWillTerminate` 首先调用 `store.flushHistory()`。 |
| `Sources/ClipShelfLite/Support/SelfTest.swift` | 合计 +137/-0 | 本批新增 6 条断言，64 条增至 70 条。 |

写入使用 `JSONEncoder.outputFormatting = [.sortedKeys]`，原因是默认 JSON 对象键顺序在不同线程/编码器实例间不稳定，曾实际出现同一数据偶发逐字节不等。排序只固定对象键顺序，不改变字段、值、数组顺序、日期/Data 编码或读取口径；旧历史兼容黑盒通过。直接编码对照使用同一明确配置，连续 5 次自检均为 70/70。

约束核对：`Package.swift` 未改，`tests/parity/` 未改，未引入第三方依赖，`history.json` 的字段和解码结构未改。

## 2. Acceptance A：构建与测试原始输出

### Debug build

```text
Building for debugging...
[1/4]
[2/5] ClipShelf-product
[5/8] ClipShelf-product
[6/8] ClipShelf-product
[8/10] ClipShelf-product
[10/10] ClipShelf-product
Build complete! (4.95秒)
```

### Release build

```text
Building for production...
[1/2] ClipShelf-product
[1/2]
[2/3] ClipShelf-product
[3/4] ClipShelf-product
[6/8] ClipShelf-product
[7/8] ClipShelf-product
Build complete! (14.33秒)
```

### Swift test

```text
Build complete! (9.93秒)
PASS_LINE_COUNT=140
◇ Test run started.
↳ Testing Library Version: 2084
↳ Target Platform: arm64e-apple-macos14.0
✔ Test run with 0 tests in 0 suites passed after 0.001 seconds.
```

说明：项目的自检由产品构造路径在两个测试产物中各执行一次，因此日志中有 140 行 `[PASS]`（70×2）；Swift Testing 本身没有另声明测试函数，所以框架汇总为 0 tests。命令退出码为 0，无 `[FAIL]`。

### Debug / Release 自检

```text
/tmp/p56-final-debug-report.md:Result: 70/70 checks passed.
/tmp/p56-final-release-report.md:Result: 70/70 checks passed.
```

稳定性复跑：

```text
/tmp/p56-stability-1.md:Result: 70/70 checks passed.
/tmp/p56-stability-2.md:Result: 70/70 checks passed.
/tmp/p56-stability-3.md:Result: 70/70 checks passed.
/tmp/p56-stability-4.md:Result: 70/70 checks passed.
/tmp/p56-stability-5.md:Result: 70/70 checks passed.
```

## 3. Acceptance B：新增 6 条断言

```text
[PASS] history writer coalesces saves and flushes the latest snapshot: Five schedules produce one atomic write, flush persists the latest snapshot, and repeated flush is idempotent
[PASS] history writer persists bytes identical to a direct encode: Background atomic persistence is byte-for-byte identical to direct JSONEncoder output
[PASS] image capture encoder reuses png bytes without touching the main thread: TIFF encoding runs off-main, preserves dimensions, yields decodable PNG, and rejects damaged data
[PASS] search thumbnail cache returns the same image twice without re-decoding: A repeated ID reuses its 104x80 thumbnail and LRU eviction honors the configured bound
[PASS] screenshot folder watcher rescans on a slow fallback interval: Event monitoring keeps a five-second fallback while screenshot name and extension rules stay intact
[PASS] fuzzy search tolerance is pinned by literal examples: Short and long fuzzy tolerances, activation threshold, exact, negative, and edit examples stay pinned
```

断言总数：`64 + 6 = 70`。

## 4. G0 专项证据

新增断言源码中的 9 组字面值：

```swift
let fuzzyLiteralResults = [
    SearchMatcher.fuzzyContains("abcd", in: "abcx"),
    SearchMatcher.fuzzyContains("abcd", in: "abxx"),
    SearchMatcher.fuzzyContains("abcdef", in: "abcdxx"),
    SearchMatcher.fuzzyContains("abcdef", in: "abcxxx"),
    SearchMatcher.fuzzyContains("ab", in: "ab"),
    SearchMatcher.fuzzyContains("abc", in: "abc"),
    SearchMatcher.fuzzyContains("meeting", in: "xxmeetingxx"),
    SearchMatcher.fuzzyContains("zzzz", in: "meetingnotes"),
    SearchMatcher.fuzzyContains("metingnotes", in: "meetingnotes")
]
```

断言期望依次为：`[true, false, true, false, false, true, true, false, true]`，失败文本会输出全部实际值。

`git diff -U0 Sources/ClipShelfLite/Support/SearchMatcher.swift` 原文：

```diff
@@ -103 +103 @@ enum SearchMatcher {
-    private static func fuzzyContains(_ query: String, in text: String) -> Bool {
+    static func fuzzyContains(_ query: String, in text: String) -> Bool {
```

除访问级别外，函数算法与常量没有任何变化。

## 5. Acceptance C：行为等价

### 历史兼容

隔离黑盒原始输出：

```text
✔ PASS T09 旧格式 history.json 可正常读取（3 条）
✔ PASS T09 旧式多路径记录未被自动拆分（最大 filePaths 长度仍为 2）
```

新写入仍使用 Codable JSON、同一字段、原子文件替换；`ClipStore.load()` 未改。对象键采用确定性排序以保证后台与直接编码逐字节可比，JSON 语义与旧文件解码兼容。

### 图片编码等价

对同一张合成 3840×2160 TIFF，旧 `NSImage`/`NSBitmapImageRep` 路径与新 ImageIO 路径实测：

```text
legacy_png_bytes=150557
current_png_bytes=150557
png_bytes_identical=true
legacy_dimensions=3840x2160
current_dimensions=3840x2160
```

本夹具逐字节一致；跨系统版本的 PNG 压缩器元数据与压缩策略不承诺永远逐字节一致，但解码尺寸和像素内容由同一源图保持。

### 截图监听全链路

使用隔离 defaults suite 与临时截图目录，先启动应用，再写入合法 `Screenshot 2026-10-03.png` 并触发文件事件：

```text
event_source_screenshot_item_count=1
event_source_chain_verified=true
```

这覆盖 `folder path → start() → DispatchSource 事件 → scan() → 稳定检查 → ClipStore 入库`，未改系统截图位置或用户历史。

## 6. Acceptance D：性能

测量方法：Release 优化构建；先用 32×32 图片预热；4K 图片和保存调度均多次测量取平均。计时脚本只位于 `/tmp`，不进入仓库。

| 场景 | 改前基线 | 改后实测 | 结果 |
| --- | ---: | ---: | --- |
| 4K 图片主线程单次阻塞 | 1017.5 ms | 0.0131 ms 平均 | ≤16 ms，通过 |
| 4K TIFF 后台编码 | 原主线程内 | 59.67 ms 平均 | 已移出主线程 |
| 连续 5 次 `save()` 主线程调度 | 约 94 ms（5×18.8） | 0.0420 ms 平均 | ≤1 ms，通过 |
| 截图目录空闲扫描 | 2 次/秒 | 0.2 次/秒 | 通过 |
| 冷启动到窗口创建后的控制端点就绪 | 未提供 | 273.06 ms 平均 | 已记录 |
| 打包二进制大小 | Debug 4,862,480 B | Release 2,939,952 B | 减少 1,922,528 B |

原始性能输出：

```text
tiff_bytes=33179970
image_handoff_ms_samples=0.0293,0.0129,0.0101,0.0080,0.0054
image_handoff_ms_average=0.0131
background_encode_ms_samples=68.30,57.01,57.54,57.83,57.65
background_encode_ms_average=59.67
five_save_schedule_ms_samples=0.0542,0.0401,0.0404,0.0423,0.0406,0.0407,0.0403,0.0409,0.0405,0.0409,0.0405,0.0395,0.0422,0.0462,0.0451,0.0406,0.0408,0.0423,0.0413,0.0407
five_save_schedule_ms_average=0.0420
writer_write_count=1
cold_start_to_control_ready_ms_samples=319.80,278.16,279.47,239.46,248.39
cold_start_to_control_ready_ms_average=273.06
release_binary_bytes=2939952
screenshot_idle_scan_per_second=0.2
```

## 7. Acceptance E：两轮隔离黑盒

### 第一轮 Debug

命令：

```bash
CLIPSHELF_PARITY_ISOLATED=1 CLIPSHELF_PARITY_DELIVERED=31,32,43,34,35,53,54,55,56,57 tests/parity/run.sh
```

原始汇总：

```text
通过 29   失败 0   跳过 1
```

跳过项仅为默认关闭的 T12 Bundle 测试，第二轮已执行。

### 第二轮 Release Bundle

命令：

```bash
CLIPSHELF_PARITY_ISOLATED=1 CLIPSHELF_PARITY_BUNDLE=1 CLIPSHELF_PARITY_DELIVERED=31,32,43,34,35,53,54,55,56,57 tests/parity/run.sh
```

原始汇总：

```text
通过 30   失败 0   跳过 0
```

两轮 T11 均通过，报告覆盖 31、32、43、34、35、53、54、55、56、57 的全部主题。

两轮 T16 原文相同：

```text
── T16 大上限 10000 不阻塞累积（prompt-32 验收 C） ──
✔ PASS T16 上限 10000 时进程未崩溃
✔ PASS T16 上限 10000 时 8 条全部累积
✔ PASS 隔离校验：用户真实 history.json 未被改动
```

## 8. Acceptance F：用户数据保护

用户日常 ClipShelf 在两轮测试期间保持运行，测试使用隔离数据目录、隔离 defaults suite、具名剪贴板和隔离控制 socket。用户 App 自身的写入不属于测试窗口；实测两个窗口内均没有写入。

### Debug 黑盒窗口

```text
window_start=2026-10-03 19:11:31 +0800
mtime=1791025735 size=3343701
c3f0c3b4b7aeba3e86babd04d668cc9bd00c433921a7d73d31943ed05c6c62e1  /Users/Zhuanz/Library/Application Support/ClipShelf/history.json

window_end=2026-10-03 19:13:26 +0800
mtime=1791025735 size=3343701
c3f0c3b4b7aeba3e86babd04d668cc9bd00c433921a7d73d31943ed05c6c62e1  /Users/Zhuanz/Library/Application Support/ClipShelf/history.json
parity_exit=0
```

### Bundle 黑盒窗口

```text
window_start=2026-10-03 19:13:41 +0800
mtime=1791025735 size=3343701
c3f0c3b4b7aeba3e86babd04d668cc9bd00c433921a7d73d31943ed05c6c62e1  /Users/Zhuanz/Library/Application Support/ClipShelf/history.json

window_end=2026-10-03 19:16:02 +0800
mtime=1791025735 size=3343701
c3f0c3b4b7aeba3e86babd04d668cc9bd00c433921a7d73d31943ed05c6c62e1  /Users/Zhuanz/Library/Application Support/ClipShelf/history.json
parity_exit=0
```

两轮 `mtime`、大小和 SHA-256 均完全一致。

## 9. G5 退出 flush 验证与自评

验证方法：启动隔离 Release 实例，通过隔离剪贴板注入文本；持续读取内存状态，刚观察到 `itemCount == 1` 时立即发送退出命令。退出前 `history.json` 尚不存在，因此最终文件只能来自 `applicationWillTerminate → flush()`。

原始输出：

```text
socket_ready=true pid=72968
inject_response={"command":"inject-text","injected":true,"ok":true}
memory_item_count_before_immediate_quit=1
history_existed_before_quit=false
quit_response={"command":"quit","ok":true,"quitting":true}
process_exited=true
history_exists_after_quit=true
persisted_item_count=1
persisted_latest_text=p56-immediate-quit-latest
flush_verified=true
```

一句话自评：六项里最没把握的是 G4，因为 TIFF/PNG 编码器跨 macOS 版本可能生成不同压缩字节；当前系统上的 4K 夹具逐字节一致，且尺寸、解码有效性和损坏输入回落都已钉住。
