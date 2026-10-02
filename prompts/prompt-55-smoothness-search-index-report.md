# prompt-55 搜索流畅度交付报告

日期：2026-10-01

## 1. 改动清单

| 文件 | 增删 | 内容 |
| --- | ---: | --- |
| `Sources/ClipShelfLite/Support/SearchIndex.swift` | +157 / -0 | 新增一次性搜索索引、按稳定 ID 缓存、可搜索字段失效、LRU 上限、查询结果缓存与后台预热入口。 |
| `Sources/ClipShelfLite/Support/SearchMatcher.swift` | +47 / -50 | 既有入口委托索引路径；保留 `matchesUnindexed`；拼音首字母由同一次拉丁转写结果派生；模糊匹配改为等价的线性 DP。 |
| `Sources/ClipShelfLite/Support/ClipKindFilter.swift` | +26 / -1 | 保持原公开签名，复用索引缓存；空查询继续走轻量类型过滤。 |
| `Sources/ClipShelfLite/Views/MainView.swift` | +43 / -1 | 用 ID、类型、查询组成廉价签名；同一组合只过滤一次；记录变化时后台预热索引；拖选快照优先级不变。 |
| `Sources/ClipShelfLite/Support/SelfTest.swift` | +107 / -0 | 新增恰好 1 条等价性断言和 3 条索引缓存断言，总数 60 → 64。 |

`tests/parity/`、`Package.swift`、`ClipItem` 及数据格式均未修改。`git diff --check` 无输出。

## 2. Acceptance A：构建与测试

### Debug 构建原始输出

```text
Building for debugging...
[2/5] ClipShelf-product
[4/6] ClipShelf-product
[6/8] ClipShelf-product
Build complete! (2.64秒)
```

### Swift Test 原始计数

```text
Build complete! (9.01秒)
swift_test_pass=128
swift_test_fail=0
Test Suite 'All tests' passed at 2026-10-01 22:37:58.508.
Executed 0 tests, with 0 failures (0 unexpected) in 0.000 (0.002) seconds
```

64 条共享断言由两条测试入口各执行一次，因此合计为 128 PASS。

### Release 构建原始输出

```text
Building for production...
[2/3] ClipShelf-product
[3/4] ClipShelf-product
[6/8] ClipShelf-product
[7/8] ClipShelf-product
Build complete! (14.47秒)
```

### 独立自检原始计数

```text
selftest_pass=64
selftest_fail=0
Result: 64/64 checks passed.
Self-test report: /tmp/p55-final-report.txt
```

## 3. Acceptance B / C：新增断言

新增断言总数恰好 4 条；全部原始通过行如下：

```text
[PASS] search index path stays equivalent to the unindexed path for pinyin queries: Every fixture and query matches identically through indexed and unindexed paths
[PASS] search index is reused across queries for the same record: Repeated access and image-only changes reuse one searchable index
[PASS] search index rebuilds when a record's searchable text changes: Replacing searchable text invalidates and rebuilds the cached index
[PASS] search index cache stays within its bound: Least-recently-used eviction keeps the index cache at its configured limit
```

等价性夹具覆盖中文全拼、首字母、子序列、模糊查询、大小写、全角、音标、空查询、类型过滤、四类可搜索字段、负例、超过 5000 字符的长文本，以及 emoji / 全角 / 带音标拉丁字符。每个夹具与每个查询都比较 `matches` 和 `matchesUnindexed`。

## 4. Acceptance D：性能

测量对象为用户真实 `history.json` 的前 100 条，只读；Release `-O` 独立基准。索引预热后，每项运行 20 次取平均。

| 场景 | 改前（规格 §1.1） | 改后实测 | 验收 |
| --- | ---: | ---: | --- |
| 冷缓存首次 `比特` | 870.4 ms | 624.593 ms | 记录数值 |
| 热缓存 `比特`，20 次平均 | 870.4 ms | 0.162 ms | ≤ 5 ms，通过 |
| 热缓存 `李永乐`，20 次平均 | 1210.3 ms | 0.179 ms | ≤ 10 ms，通过 |
| 空查询，20 次平均 | 0.10 ms | 0.006 ms | 未变慢，通过 |

原始输出：

```text
records=100
cold 比特: 624.593 ms
warm 比特 20-run average: 0.162 ms
warm 李永乐 20-run average: 0.179 ms
empty 20-run average: 0.006 ms
```

## 5. 额外改动：线性 DP 模糊匹配

### 动机

完成一次性索引后，首次复测的热缓存 `李永乐` 仍为 27.958 ms。原因是旧实现会针对长文本的每个候选窗口构造新字符串，再逐窗口计算 Levenshtein 距离。为满足既定的 ≤ 10 ms 指标，将其替换为标准的近似子串线性 DP；查询长度门槛、距离容差和布尔命中语义保持不变。

### 等价性证据

独立对照程序同时保留旧窗口算法和新 DP 算法，枚举字母表 `a`、`b`、`中`，查询长度 3...6、文本长度 queryLength...8，共比较 10,329,930 组输入。

原始输出：

```text
fuzzy-equivalence cases=10329930 result=PASS
```

此外，正式自检仍通过索引与未索引路径的全部夹具 × 查询交叉验证。

## 6. Acceptance E：隔离黑盒回归

执行命令：

```text
CLIPSHELF_PARITY_ISOLATED=1 CLIPSHELF_PARITY_DELIVERED=31,32,43,34,35,53,54,55,57 tests/parity/run.sh
```

原始汇总三行：

```text
── 汇总 ──
  通过 29   失败 0   跳过 1

```

T11 的自检退出码与主题覆盖均为 PASS。T12 打包冒烟按脚本默认策略跳过；T16 本次通过。黑盒同时输出多次 `PASS 隔离校验：用户真实 history.json 未被改动`。

## 7. Acceptance F：用户数据哈希

开始前：

```text
ce11d4f24534291fc75a41564560a1df50a87aca8f400acb12d2973ee9420c8f  /Users/Zhuanz/Library/Application Support/ClipShelf/history.json
```

收尾时：

```text
416ae215dba96918e1751256217ad9ba0e7f3c01b8e3539d8a66b65ffe6a9e6e  /Users/Zhuanz/Library/Application Support/ClipShelf/history.json
```

结论：跨整个工作窗口的哈希不一致，因此该项不能标记为通过。用户日常 ClipShelf 在测试期间持续运行，文件实测修改时间为 `2026-10-01 22:26:42 +0800`；没有执行 `pkill`，也没有回滚或写入该文件。隔离黑盒以它自己的执行前状态为基线，所有真实历史前后校验均通过，说明黑盒测试本身没有修改用户历史。由于开始时仅保存了哈希、没有复制用户文件，无法也不应恢复运行中 App 产生的真实记录变化。

## 8. 拼音首字母派生

成功从同一次 `CFStringTransform` 得到的拉丁转写串派生全拼与首字母：全拼对该串归一化，首字母直接按非字母数字切分并取首字符。没有为首字母执行第二次 `CFStringTransform`，也没有启用降级路径。

## 9. 风险自评

这次改动的主要风险是缓存失效边界与并发预热：实现只比较 `title`、`text`、`filePaths`、`sourcePath`，刻意不比较大体积 `imageData`；缓存内部以锁保护，并在插入前二次确认，避免后台预热与主线程未命中同时发生时写入过期结果。额外的线性 DP 虽经 10,329,930 组穷举对照通过，但它仍是本规格原始范围之外、最需要持续关注的算法替换点。
