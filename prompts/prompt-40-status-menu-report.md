# prompt-40 验证报告：状态菜单暂停/继续与设置

## 结果

完成。状态菜单新增同一个“暂停记录 / 继续记录”菜单项，并新增“设置”入口；两者复用现有状态和主窗口，不创建平行状态。

## 改动文件

- `Sources/ClipShelfLite/App/AppDelegate.swift`
- `Sources/ClipShelfLite/Views/MainView.swift`
- `Sources/ClipShelfLite/Support/SelfTest.swift`
- `README.md`

## 自动测试

- `swift build --disable-sandbox`：通过。
- `swift test --disable-sandbox`：通过，`34/34` 检查通过。
- `.build/debug/ClipShelf --self-test /tmp/clipshelf-p40-self-test.md`：退出码 0，`34/34` 检查通过。
- 新增断言：暂停状态拒绝采集；菜单标题由同一个启用状态映射为“暂停记录 / 继续记录”。
- 既有 32 条断言未删除、未改写。

## 手工验证步骤

1. 打开状态菜单，确认“显示 ClipShelf / 选择截图文件夹”原顺序不变，新增“暂停记录”和“设置”，最近记录区结构不变，“退出”仍为末项。
2. 点击“暂停记录”，重新打开菜单应显示“继续记录”；等待两秒并复制文本、文件、图片，历史不应新增。
3. 点击“继续记录”后再次复制，历史应新增。
4. 分别在状态菜单和设置面板切换记录开关，确认双向同步；重启后确认状态保持。
5. 点击状态菜单“设置”，确认主窗口出现且设置面板直接打开。

本轮仅授权本地检查，未覆盖安装，因此上述真实状态菜单交互留给手工验收。

## 范围说明

- 未改 `ClipboardHistoryPolicy`、持久化键、设置开关布局或状态栏图标机制。
- 现有 Evidence 行号与实现符号一致，没有发现额外漂移。
