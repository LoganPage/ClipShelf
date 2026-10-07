# Windows 1.4.9 发布记录

- 版本：1.4.9；标签：`windows-v1.4.9`。
- 主要更新：统一按钮、筛选指示器、设置/预览浮层和可见历史行的可中断动效；保留列表虚拟化，并支持系统减少动画与高对比度路径。
- 构建：`build.ps1 -SelfContained -Test`，Windows x64 自包含 Release；0 警告、0 错误，自测通过。
- 定向发布检查：动效、运行开关、类型筛选、交互、设置、拖动、焦点、UI 性能、主题过渡、预览交互、流畅度回归和更新检查共 12 组，全部返回 0。
- 性能复核：与 `179fc8a` 进行每版 20 次交错压力测试，未确认持续 UI 阻塞或新增动效性能回归；一次 55.2442 ms 回调间隔在五次相同配置复测中未复现。
- 覆盖安装：安装程序版本 1.4.9，历史与设置哈希保持不变；更新备份位于 `%LOCALAPPDATA%\ClipShelf-backups\update-20261007-163916-4b3143fc`。
- 公开包：`ClipShelf-Windows-v1.4.9-public-x64.zip`，80,002,184 字节。
- SHA-256：`E22D49336429EAEABCE9AD0D4776A0D4CAA253FEB74A48D7663981BD1029B8AE`。
- 发布页面：https://github.com/LoganPage/ClipShelf/releases/tag/windows-v1.4.9

GitHub 自动提供的 `Source code` 是源码快照，不是可直接运行的软件；Windows 用户应下载上述 `public-x64.zip`。
