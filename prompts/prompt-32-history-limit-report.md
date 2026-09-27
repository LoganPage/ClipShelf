# prompt-32 历史上限可调 - 实施报告

## 结果

已完成历史记录上限设置：范围为 1–10000，默认值为 100，并以整数形式保存到 `AppEnvironment.userDefaults` 的 `history.maxItems`。调低上限时会立即裁剪并保存历史，优先移除最旧的未置顶记录；裁剪仅删除 ClipShelf 记录，不删除任何原文件。

本轮仅完成本地实现和检查。未执行 Release 构建、覆盖安装、`git commit`、`push` 或 GitHub 发布。

## 改动文件

- `Sources/ClipShelfLite/Support/HistoryLimitPreferences.swift`
  - 新增上限偏好读写、1–10000 范围规整及统一裁剪逻辑。
- `Sources/ClipShelfLite/Stores/ClipStore.swift`
  - 从偏好读取上限；设置后立即裁剪并落盘；启动和新增记录时使用同一裁剪逻辑。
- `Sources/ClipShelfLite/Views/SettingsView.swift`
  - 在“历史”设置中增加数字输入和步进器；非法文本恢复最近合法值，越界数字收敛到合法边界。
- `Sources/ClipShelfLite/Support/SelfTest.swift`
  - 增加默认值、持久化、边界、非法输入、置顶优先裁剪和原文件保留断言。
- `README.md`
  - 在中英文功能列表中补充可配置历史上限说明。

## 自动测试

### `swift build`

- 结果：通过。

### `swift test`

- 结果：通过。
- prompt-32 相关断言：
  - 缺少设置键时默认 100。
  - 上限以整数保存，并可由新的 `UserDefaults` 实例重新读取。
  - 1 与 10000 被接受；0、-1、10001 被规整到合法范围。
  - 空串与非数字输入恢复最近合法值。
  - 调低上限时优先移除最旧的未置顶记录。
  - 移除文件记录后原文件仍存在。

### `ClipShelf --self-test <报告路径>`

- 结果：退出码 0，13/13 checks passed。
- 使用临时 `CLIPSHELF_DATA_DIR` 和临时 `CLIPSHELF_DEFAULTS_SUITE` 运行，未访问真实历史数据。

## 手工验证步骤

1. 打开设置的“历史”区域，确认“最多保留记录”初始显示 100（旧用户若已有设置则显示已保存值）。
2. 准备至少 8 条记录，其中置顶 2 条；输入 5 并按回车或移开焦点，确认立即只剩 5 条，且置顶记录被优先保留。
3. 完全退出并重新打开 ClipShelf，确认上限仍为 5，历史仍不超过 5 条。
4. 分别输入 1、10000，确认输入值被接受且应用保持可用。
5. 分别输入 0、-1、10001，确认界面最终显示 1、1、10000。
6. 输入空串或非数字文本并按回车或移开焦点，确认恢复修改前最近一次合法值。
7. 复制一个访达文件使其进入历史，再调低上限淘汰该记录；确认访达中的原文件仍然存在。
8. 在上限 10000 时继续复制文字、图片和文件，确认新记录可正常累积。

## 范围说明

- 未修改 WorkBuddy 所有的 `tests/parity/`。
- 未修改 `history.json` 的结构。
- 未改变“清空历史”“查看历史存储位置”等既有入口。
