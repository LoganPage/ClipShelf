# prompt-56：流畅度第二批 —— IO、后台轮询、图片路径、打包配置

> **本份的性质**：与 prompt-55 一样，**不是新功能，是修实测到的卡顿**。
> 与 prompt-55 的分工：**55 管「搜索与列表的纯计算」，56 管「IO / 并发 / 后台轮询 / 打包」**。**两份不要互相顺手做**。
> **行为零变化**同样是硬要求（唯一例外是 §3.1 的打包配置，那不改运行行为）。
>
> 🔴 **本份已并入 prompt-55 验收的遗留项 P2-1**（见 §3 G0）。**这是唯一一处与「不要碰 55」的例外**：
> G0 **只改 `SearchMatcher.fuzzyContains` 的访问级别（`private` → `internal`）并新增断言**，
> **它的算法与常量一个字符都不许改**。除了 G0 提到的那一处，`SearchMatcher.swift` / `SearchIndex.swift` /
> `ClipKindFilter.swift` 的其余部分**一律不要碰**。

---

## 1. 背景：实测数据（WorkBuddy 2026-10-01 实测，可复跑）

### 1.1 图片采集：TIFF → PNG 重编码（主线程，阻塞式）

`Stores/ClipStore.swift:203-218` 的 `currentImageData()` 在剪贴板**只提供 TIFF** 时（从「预览」「Safari」等 App 复制图片就是这种情况）会走：

```swift
if let tiff = pasteboard.data(forType: .tiff),
   let image = NSImage(data: tiff) {
    return pngData(from: image)      // ← 见 ClipStore.swift:335-342
}
```

`pngData(from:)` = `image.tiffRepresentation` → `NSBitmapImageRep(data:)` → `representation(using: .png)`。独立实测（`swiftc -O`，纯合成图）：

| 尺寸 | TIFF 中间产物 | 输出 PNG | **重编码耗时** |
| --- | ---: | ---: | ---: |
| 1920×1080 | 63.3 MB | 0.69 MB | **259.9 ms** |
| 2560×1600 | 125.0 MB | 1.22 MB | **570.8 ms** |
| 3840×2160 | 253.1 MB | 2.24 MB | **1017.5 ms** |

**这条路径跑在主线程上**（`pollPasteboard` 由 `Timer.scheduledTimer`（`ClipStore.swift:43-46`，0.45 s）在主 run loop 触发）。
→ **复制一张 4K 截图，界面会僵住约 1 秒**；同时瞬间分配 253 MB 的 TIFF 中间产物。

### 1.2 每次采集都全量重写整个历史（主线程）

`ClipStore.swift:411-422` 的 `save()`：`JSONEncoder().encode(items)` + `data.write(to:options:.atomic)`。
用户真实历史 **4.62 MB / 100 条**（其中 **4.32 MB 是 10 张图片的内联 base64**）。实测：

```
每次采集：JSON 编码全部记录            15.4 ms（编码后 4.62 MB）
每次采集：编码 + 原子写盘（save()）     18.8 ms
```

默认上限 `HistoryLimitPreferences.defaultValue = 100`（`Support/HistoryLimitPreferences.swift:5`），所以 **4.62 MB 就是这个量级的常态**；若历史里多几张 4K 图，单次 `save()` 会到 50–100 ms 量级。
`save()` 的调用点全部在主线程（`pollPasteboard` / 行内按钮 / 菜单 / 设置面板）。

### 1.3 列表里的图片行：每次 body 求值都重新解一次 PNG

`Views/MainView.swift:1144`（`ClipRow.preview` 的 `case .image:`；行号演变：编写时 `:1097` → 57 交付后 `:1102` → **55 交付后 `:1144`**）：

```swift
case .image:
    if let data = item.imageData, let image = NSImage(data: data) {   // ← 每次 body 都重来
```

行高固定 58 + 上下 padding 8 = 74（`historyRowHeight`），预览框只有 **52×40**。
实测解 10 张图的 `NSImage(data:)` 合计 **3.01 ms**（release），单张最大 0.29 ms —— 单看不大，但它发生在**每一次 body 求值**（选中态变化、滚动、窗口缩放、任何 `@State` 变化）上，且真正的解码成本在 `Image(nsImage:).resizable().scaledToFit()` 首次绘制时才付。

### 1.4 截图文件夹：**每 500 ms 扫一次目录**，而同一目录已经有事件监听

`Services/ScreenshotFolderWatcher.swift`：

- `:115-142` `startEventSource(for:)` —— `DispatchSource.makeFileSystemObjectSource`（`O_EVTONLY` + `.write/.extend/.attrib/.rename`）**已经在监听**该目录。
- `:144-152` `startTimer()` —— **另外**又起了一个 `repeating: .milliseconds(500)` 的定时器，回调同样调 `scan()`。
- `:154+` `scan()` —— `contentsOfDirectory` + 对每个候选文件读 `.contentModificationDate` / `.isRegularFile`。

用户桌面 **305 个条目**。即：**每秒 2 次**目录枚举 + 逐文件取属性，**永远在跑**，而事件源本来就会在变化时通知。这是纯冗余的常驻 CPU / 磁盘开销。

### 1.5 打包：**线上发的是 debug 构建**

`script/build_app_bundle.sh:25-26`：

```bash
swift build --disable-sandbox --scratch-path "$ROOT_DIR/.build" >&2
BUILD_BINARY="$(swift build --disable-sandbox --scratch-path "$ROOT_DIR/.build" --show-bin-path)/$APP_NAME"
```

**没有 `-c release`** → 默认 debug（`-Onone`）。而 `script/build_and_run.sh` → `script/install_app.sh` → `build_app_bundle.sh`，整条链都走它。
实测对比（同一提交 `c640700`）：

| | 二进制大小 | 启动解码 4.62 MB 历史 | 搜索单次（`比特`） |
| --- | ---: | ---: | ---: |
| debug（现状） | 4,862,480 B | 35.05 ms | 875.8 ms |
| **release** | **2,758,280 B** | **17.35 ms** | 870.4 ms |

- `swift build --disable-sandbox -c release` 实测 **exit 0**（20.74 s），release 二进制 `--self-test` 实测 **exit 0 / 51/51**（编写时基线；**55 交付后基线为 64/64**，本份交付时 `N` 应 ≥ 64）。
- 全项目只有两处 `#if DEBUG`，**都是调试日志**（`ScreenshotFolderWatcher.swift:290-295` 的 `debugFolderPickerLog`、`MainView.swift:1942` 起的拖选调试日志；后者行号演变：编写时 `:1871-1875` → 57 交付后 `:1900-1915` → **55 交付后 `:1942`**），编译掉无行为影响。
- ⚠️ **`-O` 对搜索那 870 ms 几乎没用**（875 → 870），那是算法问题，**由 prompt-55 负责**。本份只拿「启动更快 + 二进制小 43% + 一般代码更快」这部分收益。

---

## 2. 现状代码（Evidence）

> ⚠️ **行号会漂，以符号名为准。** 均为 2026-10-01（HEAD = `c640700`）实测。
>
> 🔁 **交付前复测（2026-10-02，HEAD = `c58bc0c`，工作区含 prompt-55 的未提交改动）**：
> - ✅ **未被 55 触碰、行号原样有效**：
>   `Stores/ClipStore.swift`（435 行）—— `start()` `:40`、Timer 0.45 s `:43`、`pollPasteboard()` `:164`、`currentImageData()` `:203`、`pngData(from:)` `:335`、`load()` `:401`、`save()` `:411`、`data.write(…, options: .atomic)` `:418`；
>   `Services/ScreenshotFolderWatcher.swift`（295 行）—— `start()` `:27`、`stop()` `:44`、`applySelectedFolder` `:92`、`startEventSource` `:115`、`startTimer()` `:144`、`.milliseconds(500)` `:146`、`scan()` `:154`；
>   `script/build_app_bundle.sh` —— 两处 `swift build` 仍在 `:25-26`，`APP_VERSION="1.4.1"` 在 `:7`。
> - ⚠️ **被 55 改动而漂移**：`Views/MainView.swift` 1984 → **2026**（`ClipRow` `:1023`、`preview` `:1136`、`case .image:` **`:1144`**、`NSImage(data: data)` `:1145`、拖选调试 `#if DEBUG` **`:1942`**）；`Support/SelfTest.swift` 1209 → **1316**（末条断言 `self test defaults suite file count does not grow` **`:1240`**、`return results` `:1246`）。
> - **漂移不均匀，一律以符号名为准。**

| 文件 | 行数 | 本份要动的地方 |
| --- | ---: | --- |
| `Stores/ClipStore.swift` | 435 | `save()` `:411-422`、`currentImageData()` `:203-218`、`pngData(from:)` `:335-342`、`pollPasteboard()` `:164-186`、`start()` `:40-46`、`load()` `:401-409` |
| `Services/ScreenshotFolderWatcher.swift` | 295 | `startTimer()` `:144-152`、`scan()` `:154+` |
| `Views/MainView.swift` | 2026（编写时 1955） | `ClipRow.preview` 的 `.image` 分支 **`:1144`**（编写时 `:1097` → 57 后 `:1102` → 55 后 `:1144`） |
| `script/build_app_bundle.sh` | — | `:25-26` |
| `Support/SearchMatcher.swift` | 132 | **仅 G0**：`fuzzyContains` 去掉 `private`（`:103`），**算法一行不动** |
| `Support/SelfTest.swift` | 1316（编写时 1056） | 新增断言（插在最后一条 `self test defaults suite file count does not grow`（**`:1240`**，编写时 `:979-984`）**之前**） |

**既有断言现状**：**64** 条（`--self-test` 报 `64/64`；`swift test` 的 `[PASS]` 是 **128 = 2 × 64**）。**51 是编写时的基线；57 追加 9 条、55 追加 4 条 → 64。本份交付后应为 64 + 6 = 70。**
**受保护断言名**（一个字不许改）：`record context menu exposes the declared actions in order`、`old history cleanup retention window only accepts the offered values`。

---

## 3. Scope（按此顺序做，每完成一项立刻自测一次）

### 3.0 G0：把 prompt-55 遗留的模糊匹配语义钉进断言（**先做这个**，零风险）

**背景**：prompt-55 把 `SearchMatcher.fuzzyContains` 从「旧算法」换成了等价的线性 DP（见 `prompts/prompt-55-verification-report.md` §3.4）。我在验收时用变异测试发现：**把它的容差常量改坏，仓库里 64 条断言全绿** —— 因为 55 新增的那条等价性断言是**相对断言**（索引路径 vs 未索引路径），两条路径**共用同一个 `fuzzyContains`**，「两边一起坏」它发现不了。

**只做两件事**：

1. `Sources/ClipShelfLite/Support/SearchMatcher.swift:103` 的 `fuzzyContains` **去掉 `private`**（改成 `static func`，即 `internal`）—— 与 55 已经对 `searchableTexts` / `normalize` / `pinyinText` / `pinyinInitials` 的处理一致。
   🔴 **只改访问级别。函数体、`distanceLimit` 的 `1 / 2`、`query.count >= 3` 的门槛，一个字符都不许动。**
2. 在 `SelfTest.swift` 新增**一条**断言（名字见 §4 B 第 6 行），用**绝对字面值**把下面 9 组结果钉死。

**这 9 组字面值我已在 2026-10-02 对当前实现逐条实跑，9/9 全部成立，可以直接抄**：

| query | text | 期望 | 它在钉什么 |
| --- | --- | --- | --- |
| `abcd` | `abcx` | `true` | 距离 1 ≤ 1 |
| `abcd` | `abxx` | `false` | 距离 2 > 1 → **钉住「短查询（≤5 字符）容差 = 1」** |
| `abcdef` | `abcdxx` | `true` | 距离 2 ≤ 2 → **钉住「长查询（≥6 字符）容差 = 2」** |
| `abcdef` | `abcxxx` | `false` | 距离 3 > 2 |
| `ab` | `ab` | `false` | 长度 2 < 3 → **钉住「< 3 字符不启用」门槛** |
| `abc` | `abc` | `true` | 长度 3 = 门槛 |
| `meeting` | `xxmeetingxx` | `true` | 子串命中 |
| `zzzz` | `meetingnotes` | `false` | 明显不匹配 |
| `metingnotes` | `meetingnotes` | `true` | 少一个 `e`，距离 1 |

**写法要求**：
- 必须是 `fuzzyContains(…) == true/false` 的**字面值比较**；**不得**在断言里用被测函数去算期望值（那是自证）。
- 9 组用 `&&` 串在同一个 `condition` 里；`failure` 文本要**指出是哪一组不成立**（把实际值拼进消息），否则下次红了不知道坏在哪。
- 这条断言是**补充**，**不得**替代或修改 55 的等价性断言（`search index path stays equivalent to the unindexed path for pinyin queries`）。

**为什么值得单独做**：`fuzzyContains` 是搜索「容错」能力的本体。没有这条断言，任何人（包括将来的我）改动它的容差，仓库里都不会有任何提示。

### 3.1 G1：打包改 release（一行改动、零代码风险）

`script/build_app_bundle.sh` 的两处 `swift build` 都加 `-c release`。

**要求**：
- 脚本里 `--show-bin-path` 那次也必须是 `-c release`（否则会指向 debug 目录，拷错二进制）。
- 打包产物必须能正常启动、图标正常、`--self-test` 仍 `64/64`（编写时基线 51/51）。
- **`install_app.sh` 会 `pkill -x ClipShelf`** —— 这是既有行为，**本份不要改**，但**你自己验证时不要真的去覆盖安装用户的应用**（用 `script/build_app_bundle.sh` 产出到 `dist/` 即可，安装由用户本人做）。
- ⚠️ **切换后必须重跑完整黑盒**（§4 E）并如实报结果：debug→release 会改变时序，**已知偶发红的 T16 有可能变好也可能变坏**，都要写进报告。

### 3.2 G2：截图文件夹去掉冗余轮询

- **保留** `startEventSource(for:)`（事件驱动）。
- **去掉** 500 ms 的重复定时器，或把它降为一个**低频兜底**（≥ 5 s）。二选一，你决定并在报告里说明理由。
- 兜底间隔必须是一个**具名常量**（例如 `minimumRescanInterval`），便于断言。
- **不得**改变 `scan()` 的判定逻辑（`isLikelyScreenshot` / `seen` / 去重 / 写库口径一律不动）。
- **不得**改变 `applySelectedFolder` / `start` / `stop` 的对外行为。

### 3.3 G3：列表图片行的缩略图缓存

- `ClipRow.preview` 的 `.image` 分支**不得**在 `body` 里直接 `NSImage(data:)`。
- 改为按 `ClipItem.ID` 缓存**小尺寸缩略图**（目标显示尺寸 52×40 pt，按 2× 生成即可），在**后台**生成，主线程只取缓存。
- 缓存要有**上限**（例如「当前历史条数 + 小余量」），超出按最久未用淘汰；`store.items` 变少时应能释放。
- 生成失败（数据损坏）要**回落到现有的占位图标**（`photo` 符号），不得崩溃。
- 🔴 **不要**缓存成 `NSImage` 之后在主线程做大图缩放；缩略图就是缩略图。

### 3.4 G4：图片采集的 PNG 重编码移出主线程

- `pollPasteboard()` 检测到剪贴板是图片后：**先在主线程完成「读 changeCount / 取原始数据」，立即返回**；TIFF→PNG 的重编码放到**后台队列**，完成后回主线程 `add(item)`。
- 重编码逻辑抽成一个**纯函数**（例如 `enum ImageCaptureEncoder { static func pngData(fromTIFF data: Data) -> Data? }`），便于断言。
- **必须保持**：① 同一张图只入库一条；② 编码失败时**回落到现有行为**（原来是返回 `nil` → 不进库），不得改成崩溃或写入空图；③ 写入剪贴板的内容与原来一致。
- **不许**用 `DispatchQueue.global().sync` 之类的「假后台」。

### 3.5 G5：`save()` 合并 + 后台写 + 退出前 flush（**最后做，风险最高**）

引入 `final class HistoryWriter`：

- 构造时可注入目标 URL 与合并窗口（便于用临时目录断言）。
- `schedule(_ items: [ClipItem])`：记录最新快照并安排一次写；**连续多次 schedule 只产生一次落盘**（合并窗口 ≤ 300 ms）。
- 内部**串行队列**保证写入顺序（后 schedule 的内容必须最后落盘）。
- 仍然使用**原子写**（`.atomic`）。
- `flush()`：**同步**等待当前排队的写完成；可重复调用、幂等。
- `writeCount`：可观测的落盘次数计数器（**仅供断言读取**）。

`ClipStore.save()` 改为调 `writer.schedule(items)`；并在**退出前**调 `flush()`（`AppDelegate` 的退出钩子，例如 `applicationWillTerminate`；如果 App 有「关闭窗口即退出」的路径，那条路径也要覆盖）。

**硬性要求**：
- 落盘内容必须与**直接** `JSONEncoder().encode(items)` 的结果**逐字节一致**（格式与字段顺序不得变）。
- `load()`（`:401-409`）的读取口径**完全不动**。
- **退出前必须 flush**，否则「复制完立刻退出」会丢数据 —— 这是本项最大的风险点，报告里必须写明你验证了这条。
- 合并窗口**不得超过 300 ms**（黑盒套件会读磁盘上的 `history.json`，窗口太大会让它变红）。

---

## 4. Acceptance

### A. 构建与测试

```bash
swift build --disable-sandbox
swift build --disable-sandbox -c release
swift test --disable-sandbox --scratch-path /tmp/cs-p56
.build/debug/ClipShelf --self-test /tmp/p56-report.txt
.build/release/ClipShelf --self-test /tmp/p56-report-release.txt
```

- 全部退出码 **0**；`swift test` **0 FAIL**；两份 `--self-test` 报告都是 `Result: N/N checks passed.` 且 `N ≥ 64`（64 = 55 交付后的基线）。
- **原始输出贴进报告**。

### B. 新增断言（**6 条**，名字必须逐字如下 —— T11 覆盖度检查依赖它们）

| 断言名 | 守什么 |
| --- | --- |
| `image capture encoder reuses png bytes without touching the main thread` | `ImageCaptureEncoder.pngData(fromTIFF:)`：合法 TIFF → 合法 PNG（能被 `NSImage(data:)` 解回、尺寸一致）；坏数据 → `nil`（不崩） |
| `history writer coalesces saves and flushes the latest snapshot` | 连续 5 次 `schedule` → `writeCount == 1`；`schedule(A) → schedule(B) → flush()` 后文件内容 == B；`flush()` 幂等 |
| `history writer persists bytes identical to a direct encode` | 落盘字节 == `JSONEncoder().encode(items)` 字节（**逐字节**） |
| `screenshot folder watcher rescans on a slow fallback interval` | 兜底间隔常量 ≥ 5 s（或定时器已移除）；并断言 `scan()` 的判定逻辑未被改动 |
| `search thumbnail cache returns the same image twice without re-decoding` | 同一 `id` 连续取两次缩略图，第二次必须是缓存命中（用可观测计数器），且缓存条目数受上限约束 |
| `fuzzy search tolerance is pinned by literal examples`（**G0**） | §3 G0 的 9 组绝对字面值（见该节表格） |

> 断言名里必须分别出现：`encode`、`writer`（两条）、`scan`、`thumbnail`、`fuzzy`。
> 全部插在 `SelfTest.swift` 最后一条断言（`self test defaults suite file count does not grow`，编写时约 `:979-984`，**55 交付后实测 `:1240`**）**之前**。

### C. 行为等价（必须有）

- `history.json` 的**格式不变**：把改动前的一份真实历史文件喂给 `load()`，解码结果必须与改动前完全一致（可断言 `ClipItem` 的 round-trip）。
- 图片采集路径：同一张 TIFF 输入，改前改后写入剪贴板的 PNG 字节一致（若无法逐字节保证，必须说明原因）。
- 截图监听：`applySelectedFolder` → `start` → 收到新截图 → 入库，全链路仍工作（可在 `tests/parity/` 之外用临时目录自测，或至少在报告里说明如何人工验证）。

### D. 性能（写进报告，用数字说话）

| 场景 | 要求 |
| --- | --- |
| 复制一张 4K 图片时，**主线程单次阻塞** | **≤ 16 ms**（改前实测 1017.5 ms 全部阻塞在主线程） |
| 连续 5 次入库的 `save()` 主线程耗时 | **≤ 1 ms**（改前每次约 18.8 ms） |
| 截图文件夹空闲时的 `scan()` 调用频率 | 从 **2 次/秒** 降到 **≤ 0.2 次/秒**（或 0，如果定时器已移除） |
| 冷启动到窗口可见 | 记录实测值（release 下应更快） |
| 打包产物 | release 二进制大小（改前 debug 4,862,480 B） |

测量方法自选，但必须：先预热、多次取平均、贴原始数字。**不得**把计时写成 `--self-test` 断言。

### E. 隔离黑盒回归（**本份必做，且要跑两轮**）

```bash
# 第一轮：debug（默认）
CLIPSHELF_PARITY_ISOLATED=1 CLIPSHELF_PARITY_DELIVERED=31,32,43,34,35,53,54,55,56,57 tests/parity/run.sh
# 第二轮：确认 G1 之后打包链仍可用（T12 会重写 Assets/*.png，属预期）
CLIPSHELF_PARITY_ISOLATED=1 CLIPSHELF_PARITY_BUNDLE=1 CLIPSHELF_PARITY_DELIVERED=31,32,43,34,35,53,54,55,56,57 tests/parity/run.sh
```

- ⚠️ **只声明「本份交付时已经落地」的编号。** 56 是本份；**55 与 57 均在本份之前交付**（57 的 commit 为 `c58bc0c`；**55 已于 2026-10-01 交付并由 WorkBuddy 验收 PASS**，`prompts/prompt-55-verification-report.md`），故一并声明。**若 55 因故未先交付，就把 55 从列表里去掉**（否则 T11 假失败）。
- 除已知的 T16 偶发红外，**0 FAIL**；T11 两条必须 PASS。
- **T16 结果必须逐字贴出来**（因为 G1 换 release、G5 改写了写盘时序，T16 是唯一可能被影响的用例）。
- 跑黑盒期间**不要并发跑 `swift build`**（这是已知会让黑盒假红的反模式）。

### F. 用户数据未被触碰

**⚠️ 口径已于 2026-10-02 修正（prompt-55 验收发现）**：用户日常的 ClipShelf **一直在运行**，它自己会写历史文件，所以「整段工作时长的哈希一致」**在结构上不可能成立**。正确口径是：

```bash
# 黑盒开始前
stat -f '%m %z' ~/Library/Application\ Support/ClipShelf/history.json
shasum -a 256 ~/Library/Application\ Support/ClipShelf/history.json
# ……跑黑盒……
# 黑盒结束后
stat -f '%m %z' ~/Library/Application\ Support/ClipShelf/history.json
shasum -a 256 ~/Library/Application\ Support/ClipShelf/history.json
```

- **判据**：**黑盒执行窗口内**该文件的 `mtime` 不得变化（任何写入都会更新 mtime）。
- 报告里要**同时贴出前后两份 `mtime` + 哈希**，并写明「**用户 App 自身的写入不属于测试窗口**」。
- 不得 `pkill` 用户正在运行的实例；不得真的执行覆盖安装。
- 若 `mtime` 恰好在窗口内变了，**必须定位到具体时刻**并说明是否与你的命令重合（用 `ps -o lstart= -p <pid>` 与命令时间戳比对）。

---

## 5. 报告要求

`prompts/prompt-56-smoothness-io-and-packaging-report.md`，必须包含：

1. 改动清单（文件 / 行数增减 / 内容），按 **G0 → G1 → G5** 分组。
2. Acceptance A 的**原始输出**（含两份 `--self-test` 的 `Result:` 行）。
3. Acceptance B 的 **6 条**新断言名与原始通过行。
4. **G0 专项**：贴出那条新断言的源码片段（含 9 组字面值），并说明 `fuzzyContains` 只改了访问级别（可用 `git diff -U0 Sources/ClipShelfLite/Support/SearchMatcher.swift` 的原文证明「只有一行变了」）。
5. Acceptance D 的**前后对比表**。
6. Acceptance E 的两轮汇总行 + **T16 原文**。
7. Acceptance F 的 **`mtime` + 哈希**前后对比（按 §4 F 的新口径）。
8. **G5 的退出 flush 你是怎么验证的**（贴命令与输出）。
9. 一句话自评：**「这六项里哪一项你最没把握？为什么？」** —— 直说。

---

## 6. 硬性约束

1. **行为零变化**（G1 除外，它只改构建配置）：搜索/筛选/剪贴板入库/去重/置顶/撤销/清理旧历史/快捷键的**对外行为一律不动**。
2. 不改 `history.json` 的格式、字段、编码方式。
3. 不加第三方依赖；不改 `Package.swift`。
4. **不许动 `tests/parity/`**（WorkBuddy 所有）。
5. **不许为了让测试通过而放宽、删除或改名任何既有断言**；两个受保护断言名一个字不许改。
6. 不引入主线程阻塞；不用 `sync` 伪装后台。
7. 只动必要文件。预期范围：`Stores/ClipStore.swift`、`Services/ScreenshotFolderWatcher.swift`、`Views/MainView.swift`、`Support/SelfTest.swift`、`Support/SearchMatcher.swift`（**仅 G0 那一处去 `private`**）、新增 `Support/HistoryWriter.swift` 与 `Support/ImageCaptureEncoder.swift`（文件名可自定，但**新增文件必须放在 `Sources/ClipShelfLite/` 目录树下**）、`script/build_app_bundle.sh`、`App/AppDelegate.swift`（仅退出 flush 的钩子）。**其他文件不要碰。**
8. **不要顺手做 prompt-55 的内容**（搜索索引 / `filteredItems` 缓存 / `SearchIndex.swift` / `ClipKindFilter.swift`）。**唯一例外是 §3 G0 那一处**：只许改 `fuzzyContains` 的访问级别 + 新增断言，**它的算法与常量一个字符都不许动**。除此之外混在一起会让「行为等价」无法判定。

---

## 7. 附：这份规格**故意没做什么**

- **没做**「把 `load()` 也移出主线程」：实测启动解码 **17.35 ms**（release），不是当前的主要瓶颈；而且异步加载会让「首屏可能闪空」变成新的行为变化。**收益小、风险中，不做。**
- **没做**「图片改存独立文件、`history.json` 只存引用」：那是**数据格式变更**，会带来迁移、清理、备份一整套问题（prompt-39 已就「缓存清理」拍板过不做同类改动）。本份只优化**计算与 IO 的位置**，不动存储结构。
- **没做**「把 0.45 s 的剪贴板轮询改成 `NSPasteboard` 的变更通知」：macOS 没有公开的剪贴板变更通知 API，只能轮询。**保持现状。**
- **没做**「搜索框防抖」：见 prompt-55 §7。
- **没做**「UI 观感 / 交互逻辑」：那是 **prompt-57**（需要用户拍板，由 WorkBuddy 出）。
