# 搜索性能与回归验证

这个独立 .NET 控制台项目只链接 `ClipItem.cs` 和 `SearchMatcher.cs`。全部数据是现场生成的中文/英文混合记录，不启动 ClipShelf 窗口，不读取历史，不访问剪贴板。

使用仓库内 SDK，从工作区根目录运行：

```powershell
& '.\.tools\dotnet\dotnet.exe' run --project windows/benchmarks/SearchBenchmark/SearchBenchmarks.csproj -c Release -- tests
& '.\.tools\dotnet\dotnet.exe' run --project windows/benchmarks/SearchBenchmark/SearchBenchmarks.csproj -c Release -- current
```

## 实测结果

`../../artifacts/search-baseline.json` 在修改搜索实现前实际采集；`../../artifacts/search-optimized.json` 在最终搜索实现上采集。每组有 100 或 1000 条记录，每条约 2000 字符，包含中文标题、较长正文及部分文件路径；相同查询连续执行两次。下表为第二次查询耗时，单位毫秒。

| 记录数 / 查询 | 修改前 | 修改后 |
| --- | ---: | ---: |
| 100 / 正文 tailneedle | 0.46 | 0.80 |
| 100 / 拼音 bianjiqi | 4.12 | 2.42 |
| 100 / 首字母 jtbls | 0.16 | 0.04 |
| 1000 / 正文 tailneedle | 14251.09 | 0.82 |
| 1000 / 拼音 bianjiqi | 14158.43 | 23.29 |
| 1000 / 首字母 jtbls | 14783.29 | 0.36 |
| 1000 / 拼写近似 accesibility | 14798.24 | 0.95 |

1000 条热查询的当前线程分配从约 160 MB 降至约 17 KB。旧缓存累计 400 个字段后整体清空，使较大数据集每轮重新生成全部拼音；新缓存跟随记录对象生命周期，删除的记录可以被回收。普通正文首次查询从 100 条 1623.62 ms / 1000 条 12836.60 ms，降至 18.94 ms / 31.26 ms，因为不再预先转写不需要的全部拼音。

首次完整拼音转写仍需要 ICU 原生计算：此压力数据下 100 条约 1.51 秒、1000 条约 13.59 秒。它只在需要时执行一次，随后复用；应在后台调用搜索，并取消已过期查询。原生 ICU 单字段调用本身不可中断，取消在其前后立即检查，以保留完整上下文转写。这里没有截断记录正文、标题、文件路径或搜索结果。

合成段落有重复文字，原有子序列匹配规则可使多种查询命中全部记录。优化前后命中数一致；这些样本用于测完整扫描与缓存，不用于衡量相关性。独立回归用例另外覆盖正例和负例。

## 验证范围

- 16000 组随机模糊匹配 / 子序列结果对照原动态规划，包括 64–128 位边界。
- 拼音全文、首字母、音调 / 大小写 / 全半角归一化、多关键词、文件和截图路径。
- 30 万字符正文末尾仍能命中；129 字符以上查询使用完整动态规划回退。
- 8 个并行读取者、首次拼音索引并发、稳定的结果顺序、记录内容改变后重新建索引。
- 预先取消、运行中取消、取消后重新检索及弱引用回收。运行中取消实测约 0.71 ms。

最终独立回归共通过 16033 项检查。测试时间不是帧率承诺；耗时会受 CPU、数据内容与系统负载影响。

## 接口

`SearchMatcher.Filter(IReadOnlyList<ClipItem> snapshot, string? query, CancellationToken token = default)` 返回原顺序的 `List<ClipItem>`。调用者在 UI 线程获得稳定的记录数组，在后台检索，并仅接收最新查询的完成结果。`Prepare(query, token)` 可进一步复用查询；既有 `Matches(item, query)` 保持兼容。
