# prompt-33 验证报告：记录类型筛选

## 实现

- 新增 `ClipKindFilter`：`all / text / file / image`，默认 `all`。
- `ClipHistoryFilter.items` 先匹配记录类型，再复用原有 `SearchMatcher`，列表、选择和批量操作继续共用 `filteredItems` 唯一漏斗。
- 搜索栏下方新增独立分段筛选行，不改变搜索栏宽度和位置。
- 切换类型时清空选择并无动画回到当前结果顶部。
- 区分“还没有记录”和“没有匹配的记录”两种空状态。

## 自动验证

- `swift build`：通过。
- `swift test`：通过，最终共享测试 `32/32`。
- `ClipShelf --self-test`：退出码 0，最终共享检查 `32/32`。
- 覆盖：四种筛选值、类型与搜索叠加、非空历史产生空筛选结果。
- `SearchMatcher.swift` 未改动。

## 实际界面验证

- 已安装版本中可见“全部 / 文字 / 文件 / 图片”筛选行。
- 选择“图片”后仅显示截图记录。
- 在图片筛选下输入无匹配关键词，显示“没有匹配的记录”。
- 切换筛选后原选择和“已选 N 条”同步清空。

## 改动文件

- `Sources/ClipShelfLite/Support/ClipKindFilter.swift`
- `Sources/ClipShelfLite/Views/MainView.swift`
- `Sources/ClipShelfLite/Support/SelfTest.swift`
- `README.md`
