# 批次 D 实施小结

## 完成状态

| 顺序 | 规格 | 状态 | 自检 |
| --- | --- | --- | --- |
| 44 | 通用文件图标中性灰与颜色断言 | 完成 | 40/40 |
| 45 | 自检 defaults 域不再增长 | 完成 | 41/41 |

最终断言总数：**41**。

本批未改动 `Views/MainView.swift`、`Stores/`、`Services/` 的任何文件，也未修改 `tests/parity/`。功能源码仅改动：

- `Sources/ClipShelfLite/Support/AppTheme.swift`
- `Sources/ClipShelfLite/Support/SelfTest.swift`

报告新增：

- `prompts/prompt-44-generic-file-type-neutral-color-report.md`
- `prompts/prompt-45-self-test-defaults-no-leak-report.md`
- `prompts/batch-D-report.md`

未执行 Release 构建、覆盖安装、`git add`、`git commit`、`git push` 或 GitHub 发布。

## 收尾 1：自检偏好文件清理

### 清理前受保护文件时间戳

```text
/Users/Zhuanz/Library/Preferences/ClipShelf.plist|1790521475|2026-09-27 23:04:35 +0800
/Users/Zhuanz/Library/Preferences/local.codex.ClipShelf.plist|1790522787|2026-09-27 23:26:27 +0800
/Users/Zhuanz/Library/Preferences/local.codex.ClipShelfLite.plist|1778948173|2026-05-17 00:16:13 +0800
```

### 首次演练完整输出

命令：`tests/parity/cleanup-selftest-prefs.sh`

```text
匹配到的遗留文件：92 个
  模式：ClipShelf.SelfTest.*.plist / ClipShelf.Runtime43.*.plist

保留不动（若存在）：
  - ClipShelf.plist
  - local.codex.ClipShelf.plist
  - local.codex.ClipShelfLite.plist

—— 以上为演练（dry run），未改动任何文件 ——
确认无误后执行： tests/parity/cleanup-selftest-prefs.sh --apply
```

### 首次应用完整输出

命令：`tests/parity/cleanup-selftest-prefs.sh --apply`

```text
匹配到的遗留文件：92 个
  模式：ClipShelf.SelfTest.*.plist / ClipShelf.Runtime43.*.plist

保留不动（若存在）：
  - ClipShelf.plist
  - local.codex.ClipShelf.plist
  - local.codex.ClipShelfLite.plist

已移入废纸篓：92 个
位置：/Users/Zhuanz/.Trash/clipshelf-selftest-prefs-20260929-145859
（可从废纸篓恢复）

剩余匹配文件：0 个
```

脚本结束后的首个即时统计曾因 cfprefsd 延迟回写显示 `1`。随后最终实现把固定域改为脚本可匹配的 `ClipShelf.SelfTest.Active`，再次验证后执行最终清理：

```text
--- final cleanup dry run ---
匹配到的遗留文件：1 个
  模式：ClipShelf.SelfTest.*.plist / ClipShelf.Runtime43.*.plist

保留不动（若存在）：
  - ClipShelf.plist
  - local.codex.ClipShelf.plist
  - local.codex.ClipShelfLite.plist

—— 以上为演练（dry run），未改动任何文件 ——
确认无误后执行： tests/parity/cleanup-selftest-prefs.sh --apply
--- final cleanup apply ---
匹配到的遗留文件：1 个
  模式：ClipShelf.SelfTest.*.plist / ClipShelf.Runtime43.*.plist

保留不动（若存在）：
  - ClipShelf.plist
  - local.codex.ClipShelf.plist
  - local.codex.ClipShelfLite.plist

已移入废纸篓：1 个
位置：/Users/Zhuanz/.Trash/clipshelf-selftest-prefs-20260929-150037
（可从废纸篓恢复）

剩余匹配文件：0 个
```

早期试验产生的精确孤立文件 `ClipShelf.SelfTest.plist` 不在脚本的 glob 范围，且 `defaults` 已确认该域未登记。为满足最终计数 0，只将该精确单文件通过 `mv -n` 移入同一废纸篓目录；未使用 `rm` 或通配符：

```text
moved exact orphan: ClipShelf.SelfTest.plist -> /Users/Zhuanz/.Trash/clipshelf-selftest-prefs-20260929-150037
--- required final self-test count after exact orphan move ---
0
```

最终按要求执行：

```text
ls -1 ~/Library/Preferences/ | grep -c '^ClipShelf\.SelfTest\.'
0
```

### 清理后受保护文件时间戳

```text
/Users/Zhuanz/Library/Preferences/ClipShelf.plist|1790521475|2026-09-27 23:04:35 +0800
/Users/Zhuanz/Library/Preferences/local.codex.ClipShelf.plist|1790522787|2026-09-27 23:26:27 +0800
/Users/Zhuanz/Library/Preferences/local.codex.ClipShelfLite.plist|1778948173|2026-05-17 00:16:13 +0800
```

前后 epoch 与可读时间戳完全一致。

## 收尾 2：Assets 图标清理

### `git status --porcelain` 清理前

```text
 M Assets/AppIcon1.iconset/icon_512x512@2x.png
 M Assets/AppIcon2.iconset/icon_512x512@2x.png
 M Assets/AppIcon3.iconset/icon_512x512@2x.png
 M Assets/AppIcon4.iconset/icon_512x512@2x.png
 M Assets/AppStatusIcon.png
 M Sources/ClipShelfLite/Support/AppTheme.swift
 M Sources/ClipShelfLite/Support/SelfTest.swift
?? .build
?? .workbuddy-ai/
?? ClipShelf-GPT-Upload-2026-05-19.zip
?? ClipShelf-GPT-Upload-2026-05-19/
?? docs/
?? prompts/README.md
?? prompts/_TEMPLATE.md
?? prompts/batch-B.md
?? prompts/batch-B2-verification-report.md
?? prompts/batch-C-verification-report.md
?? prompts/batch-D.md
?? prompts/prompt-31-verification-report.md
?? prompts/prompt-32-verification-report.md
?? prompts/prompt-34-verify-report.md
?? prompts/prompt-35-verify-report.md
?? prompts/prompt-39-cache-cleanup.md
?? prompts/prompt-43-verification-report.md
?? prompts/prompt-44-generic-file-type-neutral-color-report.md
?? prompts/prompt-44-generic-file-type-neutral-color.md
?? prompts/prompt-45-self-test-defaults-no-leak-report.md
?? prompts/prompt-45-self-test-defaults-no-leak.md
?? tests/
```

唯一执行的 Assets 清理命令：

```text
git checkout -- Assets/
```

### `git status --porcelain` 清理后

```text
 M Sources/ClipShelfLite/Support/AppTheme.swift
 M Sources/ClipShelfLite/Support/SelfTest.swift
?? .build
?? .workbuddy-ai/
?? ClipShelf-GPT-Upload-2026-05-19.zip
?? ClipShelf-GPT-Upload-2026-05-19/
?? docs/
?? prompts/README.md
?? prompts/_TEMPLATE.md
?? prompts/batch-B.md
?? prompts/batch-B2-verification-report.md
?? prompts/batch-C-verification-report.md
?? prompts/batch-D.md
?? prompts/prompt-31-verification-report.md
?? prompts/prompt-32-verification-report.md
?? prompts/prompt-34-verify-report.md
?? prompts/prompt-35-verify-report.md
?? prompts/prompt-39-cache-cleanup.md
?? prompts/prompt-43-verification-report.md
?? prompts/prompt-44-generic-file-type-neutral-color-report.md
?? prompts/prompt-44-generic-file-type-neutral-color.md
?? prompts/prompt-45-self-test-defaults-no-leak-report.md
?? prompts/prompt-45-self-test-defaults-no-leak.md
?? tests/
```

测试期间临时出现的 `.build` 链接随后已撤销，原派生目录已原样恢复；最终状态不再包含 `.build`，也不再包含任何 `Assets/` 修改。

## 遗留问题

1. cfprefsd 在进程退出后仍可能异步写回一个固定的 `ClipShelf.SelfTest.Active.plist`。实现保证它不会按运行次数增长；现有安全清理脚本也能匹配并移动该固定文件。
2. 仓库位于受文件提供器管理的 Documents 目录时，测试 bundle 会被自动附加 `FinderInfo`，导致签名失败。本批仅把被忽略的 `.build` 临时链接到 `/tmp` 完成规定测试，结束后已恢复原状；未改产品代码或测试来掩盖环境问题。
3. 工作区仍有用户原有的未跟踪文件和目录，本批未清理、未暂存、未提交。
