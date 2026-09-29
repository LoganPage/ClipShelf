# 批次 C 实施小结

## 完成状态

| 顺序 | 规格 | 状态 | 最终断言数 |
|---|---|---|---:|
| 1 | prompt-40 状态菜单暂停/继续与设置 | 完成 | 34 |
| 2 | prompt-41 图片预览缩放 | 完成 | 35 |
| 3 | prompt-36 图片按需 OCR | 完成 | 37 |
| 4 | prompt-42 文本预览增强 | 完成 | 39 |

执行顺序严格为 `40 → 41 → 36 → 42`，没有并行铺开或调换。

## 各阶段检查

每一项完成后均立即运行：

```text
swift build --disable-sandbox
swift test --disable-sandbox
.build/debug/ClipShelf --self-test <报告路径>
```

- prompt-40：构建通过；`swift test` 与自检均为 `34/34`。
- prompt-41：构建通过；`swift test` 与自检均为 `35/35`。补充窗口尺寸监听后重新运行，结果不变。
- prompt-36：构建通过且无警告；`swift test` 与自检均为 `37/37`。消除 Vision 并发兼容警告后重新运行，结果不变。
- prompt-42：首次构建失败，随后修复 SDK 常量、代理协议和类型推断；使用遇错即停重新运行后，构建通过且无警告，`swift test` 与自检均为 `39/39`。

基线 32 条断言全部保留，本批只新增 7 条：prompt-40 两条、prompt-41 一条、prompt-36 两条、prompt-42 两条。

## 硬性分工核对

- 图片缩放控件位于右下角浮层；OCR 入口位于顶部工具条。
- `Cmd+= / Cmd+- / Cmd+0 / Cmd+滚轮` 与 `Cmd+F / F3 / Shift+F3 / Esc` 共存。
- 键盘和滚轮本地监听均在 `stopKeyMonitor()` 中移除；红色关闭按钮也会清理预览会话。
- `showBuiltInPreviewIfPossible(_:)`：`.image` 的缩放入口归 41；`.text` 与文本文件回落归 42；36 未改该函数。
- `.file` 图片判定始终位于文本扩展名判定之前。

## Evidence 差异

- 开始时仓库 HEAD 与批次文件记录一致，均为 `0860ab3`；相关符号存在，行号在本批串行改动后自然漂移。
- 当前 SDK 头文件定义 GB18030 的 Core Foundation 编码值，但 Swift 模块未导出命名常量；实现使用对应系统常量值 `0x0632` 经 `CFStringConvertEncodingToNSStringEncoding` 转换，并由中文样本断言验证。
- prompt-42 所述 Esc 关闭预览原本来自响应链而非键盘监听；实现仅在查找条打开时吞掉 Esc，关闭时仍交还响应链。真实窗口行为尚待手工复核。

## 未完成与手工项

功能代码与自动检查均完成，没有代码阻塞项。由于本轮明确仅本地检查、禁止覆盖安装，以下图形交互没有伪报为通过：

- 状态菜单动态标题、双向同步和设置面板直达。
- 图片缩放手感、中心锚定和大图 400% 性能。
- 真实图片 OCR、拖框区域与未点击不识别的运行时观测。
- 文本行号/换行/查找条、20 MB 文件和 Esc 响应链。

## 授权范围

- 未执行 Release 构建。
- 未覆盖安装。
- 未执行 `git commit`、`git push` 或 GitHub 发布。
- 未修改 `tests/parity/`、历史数据结构或既有 32 条断言。
