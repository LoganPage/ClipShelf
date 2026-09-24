# Windows 1.3.5 本地发布候选记录

- 源码功能提交：`463a99f`（预览预热、缩放与专项验证）；版本：1.3.5。
- 构建：`build.ps1 -SelfContained`，Windows x64 独立运行包；Release 构建 0 警告、0 错误。
- 测试对齐后的最终本地 ZIP：`dist/ClipShelf-Windows-v1.3.5-aligned-final-local-x64.zip`，79,985,775 字节；先前 ZIP 保留作对照，不代表当前构建。
- 当前 ZIP SHA-256：`0C7119A3FC06FB6AF7444AA132F8A13F798E83F13A4F2987E31A036886D9EC13`。
- 安装验证：`artifacts/v1.3.5-aligned-install-final.json`；程序成功启动，历史与设置在正常退出后的稳定状态至安装完成期间哈希未变，更新备份保留。
- 完整执行 25 组现有测试：25 组、2,851 项全部通过；最终报告见 `artifacts/v1.3.5-alignment-final2/`。失败、缺失或损坏报告现在都返回非零退出码，并留下 `passed: false` 报告；三种受控失败探针已验证。
- 1.3.4 基线及对齐前的 1.3.5 曾有布局、按钮圆角、图标和交互旧断言失败；逐项判定与实测数据见 [测试对齐记录](windows-v1.3.5-test-alignment.md)。本轮只修改测试与验证脚本，无用户可见行为变化，故版本仍为 1.3.5。
- 这是本地发布候选，不代表已发布到 GitHub；公开下载页面仍指向已经发布的 Windows 1.3.4。
- ZIP、测试报告和用户数据均不进入 Git；不删除任何历史备份。
