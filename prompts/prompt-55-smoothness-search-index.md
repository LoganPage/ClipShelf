# prompt-55：搜索流畅度 —— 一次性索引 + 结果缓存（**行为零变化**的性能修复）

> **本份的性质**：这不是新功能，是**修一个已被实测证实的严重性能缺陷**。
> 判定标准只有两条：① **行为必须完全等价**（同一条记录、同一个查询，改前改后命中结果必须一模一样）；② **耗时必须大幅下降**。
> ①不成立 = 返工；②不成立 = 返工。

---

## 1. 背景：实测数据（WorkBuddy 2026-10-01 实测，可复跑）

用**用户真实的** `~/Library/Application Support/ClipShelf/history.json`（**4.62 MB / 100 条**，其中 **4.32 MB 是 10 张图片的内联 base64**，85 条文字合计仅 0.04 MB）做的独立基准（编译 `SearchMatcher.swift` + `ClipItem.swift` + `ClipKindFilter.swift` 三个文件到 `/tmp` 里跑，**不改动产品代码**）：

### 1.1 单次 `ClipHistoryFilter.items(items, kind: .all, query:)`（100 条）

| 查询 | debug(-Onone) | **release(-O)** |
| --- | ---: | ---: |
| （空查询） | 0.21 ms | 0.10 ms |
| `比特` | 875.8 ms | **870.4 ms** |
| `btc` | 637.1 ms | **575.2 ms** |
| `截图` | 1058.1 ms | **1039.6 ms** |
| `李永乐` | 2689.7 ms | **1210.3 ms** |

> **注意**：`-O` 几乎没帮上忙（`比特` 875 → 870 ms）。**这不是优化等级问题，是算法问题。**

### 1.2 耗时构成（对一条 **8310 字符**的文字记录，单次调用）

```
normalize              1.003 ms
pinyinText            41.912 ms     ← CFStringTransformToLatin + StripDiacritics
pinyinInitials        41.906 ms     ← 又做了一遍同样两次 CFStringTransform
isSubsequence          0.163 ms
fuzzyContains          0.000 ms     （2 字查询不触发）
matches(整条)        172.959 ms
```

**根因非常明确**：

1. `SearchMatcher.pinyinText` 与 `SearchMatcher.pinyinInitials`（`Support/SearchMatcher.swift:43-48` / `:50-60`）**各自完整做了一遍** `kCFStringTransformToLatin` + `kCFStringTransformStripDiacritics` —— **100% 重复劳动**，一次调用里同样的 ICU 转写跑两遍。
2. 这两个函数在 `matches` 里对 `searchableTexts(for:)` 的**每一个字符串**都要调一遍（最多 4 个：`title` / `text` / `filePaths…` / `sourcePath`）。
3. **更致命的是调用次数**：`MainView.filteredItems` 是**计算属性**，每次访问都重算一遍全量过滤。而 `body` 里它被访问多次：

   | 访问点（`Views/MainView.swift`，行号会漂，以符号名为准） | 次数 |
   | --- | --- |
   | `if filteredItems.isEmpty`（空态判断，`:55`） | 1 |
   | `ForEach(Array(filteredItems.enumerated()), …)`（`:150`） | 1 |
   | `ForEach` 闭包内的 `filteredItems.count` / `filteredItems[index + 1]`（`:152` / `:160`，每建一行 2 次） | 2 × 可见行数 |
   | `DragSelectionCaptureView(itemIDs: filteredItems.map(\.id))`（`:190` / `:193`） | 1 |

   搜索框是普通 `TextField("搜索文字、文件名、截图名", text: $searchText)`（`Views/MainView.swift:368`；编写时为 `:360`，57 交付后 **+8**），**没有任何防抖** → **每敲一个字符**都会让整个 `MainView.body` 重算，于是上面这一串访问**每个键各跑一遍**。

> **保守结论**：即使只按「每次 body 求值访问 3 次」算，敲一个字符也要 **2.6 秒**；按代码计数（约 10–19 次）算是 **9–17 秒**。
> 具体次数由 SwiftUI 决定、我方未做端到端计时，**不要把这段倍数当成实测值**；但**单次 0.87 秒是实测值**，问题成立与否不依赖那个倍数。

### 1.3 为什么现在必须修

- 用户 2026-10-01 明确提出「**功能既然都对齐 win 了，那就应该优化流畅度和 UI 交互逻辑了**」。
- 这份是**流畅度里收益最大、风险最低**的一处：**纯计算，不碰 IO、不碰并发、不碰数据格式**。
- 而且它有**明确的对错判据**：改前改后的命中结果必须逐条一致。

---

## 2. 现状代码（Evidence）

> ⚠️ **行号会漂，以符号名为准。** 下面行号均为 2026-10-01（HEAD = `c640700`）实测。
>
> 🔁 **交付前复测（HEAD = `c58bc0c`，prompt-57 已交付）**：`SearchMatcher.swift`（134 行）与 `ClipKindFilter.swift`（37 行）**未被 57 触碰，下列行号全部原样有效**。漂移只发生在 `MainView.swift`（1955 → **1984**）与 `SelfTest.swift`（1056 → **1209**），已就地更正（§2.3 / §2.5 / §1.2）。**注意漂移不均匀**（同文件内 +1 与 +8 并存），**一律以符号名为准，不要按固定偏移换算**。

### 2.1 `Sources/ClipShelfLite/Support/SearchMatcher.swift`（全 **134** 行）

```swift
enum SearchMatcher {
    static func matches(_ item: ClipItem, query rawQuery: String) -> Bool {   // :4
        let query = normalize(rawQuery)
        guard !query.isEmpty else { return true }
        return searchableTexts(for: item).contains { text in
            let normalized = normalize(text)
            let pinyin = pinyinText(text)        // ← 每次调用都重算
            let initials = pinyinInitials(text)  // ← 每次调用都重算（且重复上面那两步 ICU）
            return normalized.contains(query)
                || pinyin.contains(query)
                || initials.contains(query)
                || isSubsequence(query, of: normalized)
                || isSubsequence(query, of: pinyin)
                || isSubsequence(query, of: initials)
                || fuzzyContains(query, in: normalized)
                || fuzzyContains(query, in: pinyin)
        }
    }

    private static func searchableTexts(for item: ClipItem) -> [String] { … }   // :24-34
    private static func normalize(_ value: String) -> String { … }              // :36-41
    private static func pinyinText(_ value: String) -> String { … }             // :43-48
    private static func pinyinInitials(_ value: String) -> String { … }         // :50-60
    private static func isSubsequence(_ needle: String, of haystack: String) -> Bool { … }  // :62-80
    private static func fuzzyContains(_ query: String, in text: String) -> Bool { … }       // :82-103
    private static func levenshtein(_ lhs: String, _ rhs: String, maxDistance: Int) -> Int { … }  // :105-133
}
```

**语义要点（改动时必须逐条保持）**：

- `searchableTexts(for:)` 的顺序是 `[title, text?, filePaths…, sourcePath?]`，用 `contains` 短路（**顺序影响短路时机，不影响最终布尔值**）。
- `normalize` = `folding([.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: .current).lowercased()` 再**只保留字母与数字**。
- `isSubsequence` 对空 needle 返回 `true`；`fuzzyContains` 只在 `query.count >= 3` 且 `text.count >= query.count` 时工作，容差 `query.count <= 5 ? 1 : 2`。
- **空查询直接返回 `true`**（在 `normalize(rawQuery)` 之后判断）。

### 2.2 `Sources/ClipShelfLite/Support/ClipKindFilter.swift:30-37`

```swift
enum ClipHistoryFilter {
    static func items(_ items: [ClipItem], kind: ClipKindFilter, query: String) -> [ClipItem] {
        let trimmedQuery = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return items.filter { item in
            kind.matches(item) && SearchMatcher.matches(item, query: trimmedQuery)
        }
    }
}
```

### 2.3 `Sources/ClipShelfLite/Views/MainView.swift:39-45`（编写时为 `:38-44`，57 交付后 **+1**）

```swift
private var liveFilteredItems: [ClipItem] {
    ClipHistoryFilter.items(store.items, kind: kindFilter, query: searchText)
}

private var filteredItems: [ClipItem] {
    dragSnapshotItems ?? liveFilteredItems
}
```

### 2.4 `Sources/ClipShelfLite/Models/ClipItem.swift`（全 **66** 行）

`ClipItem` 是 `Identifiable, Codable, Hashable` 的**值类型**；`imageData: Data?` 最多 0.97 MB。

> 🔴 **这是本份最危险的一个坑，务必看**：`ClipItem` 的 `==` 是合成的 → **会逐字节比较 `imageData`**。
> 所以 **禁止** 用 `.onChange(of: store.items)` / `.onChange(of: store.items.map { $0 })` 这类「拿整个数组做 Equatable 比较」的写法来判断「要不要重算」—— 那会在每次变化时 memcmp 最多 4.3 MB。
> 用 §3.4 的**廉价签名**。

### 2.5 断言底座

`Sources/ClipShelfLite/Support/SelfTest.swift` 现有 **60** 条断言（`--self-test` 报 `60/60`；`swift test` 的 `[PASS]` 是 **120 = 2 × 60**）。**51 是编写时的基线，prompt-57 已追加 9 条。** 最后一条仍是 `self test defaults suite file count does not grow`（编写时 `:979-984`，**57 交付后实测 `:1133`**）。**新增断言一律插在它之前。**

---

## 3. Scope

### 3.1 新增 `Sources/ClipShelfLite/Support/SearchIndex.swift`

```swift
/// 一条记录的搜索索引：把「归一化 / 全拼 / 首字母」三种形态**各算一次**并缓存。
struct ClipSearchIndex: Equatable {
    struct Field: Equatable {
        let normalized: String
        let pinyin: String
        let initials: String
    }

    /// 与 `SearchMatcher.searchableTexts(for:)` **一一对应、顺序一致**。
    let fields: [Field]

    /// 构造时算一次（内部调 `SearchMatcher` 的既有算法，不得另写一套）。
    init(item: ClipItem)
}
```

**硬性要求**：

1. `init(item:)` 内部**必须复用** `SearchMatcher` 里既有的 `normalize` / `pinyinText` / `pinyinInitials` 算法（把它们从 `private` 放宽到 `internal` 即可），**不许另写一份归一化/拼音实现** —— 否则「等价」就成了两套实现互相比对，失去意义。
2. `pinyinInitials` 必须**从 `pinyinText` 已经算出的结果派生**（对那个已转写成拉丁字母的串做切分取首字母），**不得再做一次 `CFStringTransform`**。这是本份最大的一处浪费（见 §1.2）。若你发现做不到（例如派生结果与现有实现在某些字符上不一致），**如实报告并保留原实现**，不要偷偷降级。

### 3.2 `SearchMatcher` 增加「按索引匹配」的入口，并保留原实现做参照

```swift
enum SearchMatcher {
    // 既有签名保持不变，改为委托给索引路径
    static func matches(_ item: ClipItem, query: String) -> Bool {
        matches(ClipSearchIndex(item: item), query: query)
    }

    /// 新增：用已算好的索引做匹配（击键路径只走这里）
    static func matches(_ index: ClipSearchIndex, query rawQuery: String) -> Bool { … }

    /// 新增：**把现在这份逐条重算的实现原样保留下来**，仅供断言做「两条路径结果一致」的交叉验证。
    /// 名字必须含 `Unindexed`。生产代码不得调用它。
    static func matchesUnindexed(_ item: ClipItem, query: String) -> Bool { … }
}
```

> `matchesUnindexed` 是**本份的安全网**：它让「索引路径」和「原路径」可以在断言里对所有夹具 × 所有查询逐一对齐。
> **不许**把 `matches(_ item:query:)` 实现成直接调 `matchesUnindexed`（那就白做了），也**不许**删掉 `matchesUnindexed`。

### 3.3 跨击键的索引缓存

新增一个**引用类型**（`final class`）缓存，按 `ClipItem.ID` 存 `ClipSearchIndex`：

- 命中：直接取用，**不做任何重算**。
- 未命中：算一次并存入。
- **失效**：`ClipItem` 的 `text` / `title` / `filePaths` / `sourcePath` 若与建索引时不同（值类型可能被整体替换），必须重算。**校验这些字段是廉价的字符串比较，绝不比较 `imageData`。**
- 上限：缓存条目数不得超过当前历史条数 + 一个小余量（例如 `maxItems + 8`），超出按最久未用淘汰。

### 3.4 `MainView`：`filteredItems` 一次变化只算一次

要求：**同一个 `(store.items, kindFilter, searchText)` 组合下，全量过滤只允许执行一次**，`body` 里所有访问点读的都是缓存结果。

推荐做法（可自选等价方案，但必须满足上面这条不变量）：

```swift
@State private var filteredItemsCache: [ClipItem] = []

/// 廉价签名：只比 id 序列 + 类型 + 查询。**绝不包含 ClipItem 本体**（见 §2.4）。
private struct SearchSignature: Equatable {
    let ids: [ClipItem.ID]
    let kind: ClipKindFilter
    let query: String
}
```

- 在 `body` 里用 `.onChange(of: signature) { … }` 重算一次；初始值在 `.task` / `.onAppear` 里算一次。
- `filteredItems` 改为读 `filteredItemsCache`（`dragSnapshotItems` 的优先级**必须保持不变**）。
- 🔴 **不许**用 `.onChange(of: store.items)`（§2.4）。

### 3.5 索引预热（必须做，否则第一次搜索仍会卡）

`store.items` 变化时（含启动加载完成后），把新增记录的索引**在后台队列**建好再放回缓存；击键路径只做缓存查找。
缓存未命中时仍要**正确**（就地算，宁可慢一次），**不许**为了快而返回空结果或跳过匹配。

---

## 4. 硬性约束

1. **行为零变化**：对任何 `(ClipItem, query)`，`SearchMatcher.matches` 的返回值改前改后必须完全相同。这是本份的第一验收项。
2. **不改对外语义**：`ClipHistoryFilter.items` 的签名与结果不变；`searchableTexts` 的字段集合与顺序不变；空查询仍返回全部（受 `kind` 过滤）。
3. **不加第三方依赖**；不改 `Package.swift`（`path: "Sources/ClipShelfLite"` 是目录通配，新增文件不需要改 manifest）。
4. **不改数据格式**：`history.json` 的字段与编码方式一律不动，`ClipItem` 的 `Codable` 实现不动。
5. **不许动 `tests/parity/`**（WorkBuddy 所有）。
6. **不许为了让测试通过而放宽、删除或改名任何既有断言**；受保护的两个名字（`record context menu exposes the declared actions in order`、`old history cleanup retention window only accepts the offered values`）一个字不许改。
7. **不引入新的主线程阻塞**：新增的后台工作必须真的在后台（不得用 `DispatchQueue.global().sync`）。
8. 只动**必要**的文件。预期是：新增 `Support/SearchIndex.swift`；改 `Support/SearchMatcher.swift`、`Support/ClipKindFilter.swift`（如需）、`Views/MainView.swift`、`Support/SelfTest.swift`。**其他文件不要碰。**

---

## 5. Acceptance

### A. 构建与测试

```bash
swift build --disable-sandbox
swift test --disable-sandbox --scratch-path /tmp/cs-p55
swift build --disable-sandbox -c release
.build/debug/ClipShelf --self-test /tmp/p55-report.txt
```

- 全部退出码 **0**；`swift test` **0 FAIL**；`--self-test` 报告 `Result: N/N checks passed.` 且 `N ≥ 60`（60 = 57 交付后的基线）。
- **报告里贴原始输出**（`Build complete!`、`PASS/FAIL` 计数、`Result:` 行）。

### B. **等价性**（本份最重要的一项，必须有断言：**恰好 1 条**）

新增**一条**断言，断言名**必须含 `index`、`equivalent`、`pinyin`** 三个词（例：`search index path stays equivalent to the unindexed path for pinyin queries`），做法：

- 构造一组夹具 `(ClipItem, query)` 覆盖：中文全拼命中、中文首字母命中、子序列命中、模糊匹配命中（`query.count >= 3` 走 Levenshtein）、大小写/全角/带音标、空查询、`kind` 过滤、`title`/`text`/`filePaths`/`sourcePath` 四个字段各自命中、**不命中**的负例。
- 对**每一个**夹具 × **每一个**查询，断言：

  ```swift
  SearchMatcher.matches(item, query: q) == SearchMatcher.matchesUnindexed(item, query: q)
  ```

- 夹具里**至少要有一条长文本**（≥ 5000 字符）与**一条含 emoji / 全角 / 带音标拉丁字母**的记录。
- 失败信息里要列出**具体是哪个夹具 + 哪个查询**不一致。

### C. 索引缓存正确性（必须有断言：**恰好 3 条**，断言名都必须含 `index`）

> 建议名（可微调措辞，但**必须保留 `index` 这个词**）：
> `search index is reused across queries for the same record`、
> `search index rebuilds when a record's searchable text changes`、
> `search index cache stays within its bound`。

- 同一 `ClipItem` 连续取两次索引，第二次必须是缓存命中（可用一个可观测的计数器，例如 `ClipSearchIndexCache.buildCount`，**该计数器只在断言里读**）。
- 同一 `id` 但 `text` 被替换后，索引必须重建（断言新索引对替换后的文本命中）。
- 缓存条目数不得超过上限（断言上限生效）。

**本份断言总数：60 → 64**（B 的 1 条 + C 的 3 条；**60 是 prompt-57 交付后的基线**，编写时为 51 → 55）。若你认为需要拆成更多条，可以，但**必须 ≥ 4 条**且 `--self-test` 报告里能命中 §4 要求的全部关键词。

### D. 性能（写进报告，用数字说话）

用 **100 条规模**的夹具（可复制同一条记录 100 次，或直接读用户真实历史——**只读，不得写**），测：

| 场景 | 要求 |
| --- | --- |
| **冷缓存**首次 `ClipHistoryFilter.items(…, query: "比特")` | 记录实测值（允许较慢，但要报数） |
| **热缓存**同一个查询再跑 20 次的平均 | **必须 ≤ 5 ms**（改前实测 870 ms） |
| **热缓存**查询 `李永乐`（3 字，走模糊匹配） | **必须 ≤ 10 ms**（改前实测 1210 ms） |
| 空查询 | 不得比改前更慢 |

- 测量方法自选，但**必须**：先预热一轮再计时、至少 20 次取平均、把原始数字贴进报告。
- **不得**把「计时」写成 `--self-test` 的断言（计时不稳定，会造成假红）。

### E. 隔离黑盒回归

```bash
CLIPSHELF_PARITY_ISOLATED=1 CLIPSHELF_PARITY_DELIVERED=31,32,43,34,35,53,54,55,57 tests/parity/run.sh
```

- ⚠️ **只声明「本份交付时已经落地」的编号。** 55 是本份（要交付的），**57 已在本份之前交付**（commit `c58bc0c`），故一并声明；**56 尚未交付，不要写进去**（写进去 T11 会因缺关键词而**必然失败**，那是假失败）。
- **除已知的 T16 偶发红之外**，0 FAIL；T11 两条必须 PASS。
- 报告里贴「汇总」三行。

### F. 用户数据未被触碰

- 跑完 `shasum -a 256 ~/Library/Application\ Support/ClipShelf/history.json`，与**开始前**一致。
- 不得 `pkill` 用户正在运行的实例。

---

## 6. 报告要求

`prompts/prompt-55-smoothness-search-index-report.md`，必须包含：

1. 改动清单（文件 / 行数增减 / 内容）。
2. Acceptance A 的**原始输出**。
3. Acceptance B / C 的断言名与**原始通过行**。
4. Acceptance D 的**前后对比表**（改前数字直接引用本份 §1.1，改后是你自己测的）。
5. Acceptance E 的汇总行。
6. Acceptance F 的哈希前后对比。
7. 一句话自评：**「你觉得这次改动的风险点在哪里？」** —— 直说，不要客套。
8. 若 `pinyinInitials` 无法从拼音串派生（§3.1 第 2 条），**明确写出**并说明保留了什么。

---

## 7. 附：这份规格**故意没做什么**

- **没做**「给搜索框加防抖」：加了索引之后击键路径本来就是亚毫秒级，防抖只会引入「输入延迟」这种**用户可感知的行为变化**。**行为零变化是本份的前提。**
- **没做**「截断长文本的拼音索引长度」：那会**改变匹配结果**（超出截断长度的内容将不再命中），属于行为变化。本份靠**缓存**解决重复计算，不靠截断。
- **没做**「把 `filteredItems` 换成 `@Published` 的 ViewModel」：那要动 `ClipStore` 的发布面，风险大于收益。本份用 `MainView` 内的 `@State` 缓存。
- **没做**「`save()` 移出主线程 / 图片缩略图缓存 / 截图文件夹 500ms 轮询 / 打包改 release」：这四项**同样是实测到的流畅度问题**，但都属于「IO 与并发」或「打包配置」，**另出 prompt-56**。本份**不要顺手做**，否则等价性验收会被搅浑。
