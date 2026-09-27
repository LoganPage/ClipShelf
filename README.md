# ClipShelf

## 中文

ClipShelf 是一个简洁的 macOS 剪贴板历史工具，支持文字、文件和截图记录。它适合想让普通 macOS 截图自动进入剪贴板，同时继续保留原截图文件的人。

### 功能

- 记录文字、文件和图片剪贴板历史
- 自动排除密码管理器等标记为敏感或临时的剪贴板内容，不写入历史
- 历史记录上限可在 1–10000 条之间调整，调低后优先保留置顶记录
- 监听截图文件夹，新截图自动复制到剪贴板并加入历史
- 清空历史时只删除 ClipShelf 记录，不删除截图文件夹或访达里的原文件
- 支持搜索、键盘上下选择、回车粘贴、空格预览
- 支持单选、多选、批量删除、Command-A/C/V
- 支持多选后的点击行为自定义
- 支持三指拖移多选，并可调整点击恢复期
- 支持记录置顶，置顶记录会固定显示在普通记录前面
- 支持自定义全局呼出快捷键、取消选择快捷键、置顶选中记录快捷键
- 支持开机自启动
- 支持四种 App 图标方案
- GitHub Releases 有新版本时，主界面会显示更新按钮

### 系统要求

- macOS 13 或更新版本

### 使用前设置

为了让截图后立即进入剪贴板，请在 macOS 截图设置里：

1. 将截图保存位置设置为 ClipShelf 设置里的同一个截图文件夹
2. 关闭 macOS 截图工具里的“显示浮动缩略图”选项

这样截图会继续保存为文件，同时也会自动进入 ClipShelf 和剪贴板。

### 从源码运行

```bash
swift build
```

打包成本机 App：

```bash
./script/build_app_bundle.sh
```

安装到 `~/Applications` 并启动：

```bash
./script/install_app.sh
```

### 隔离运行时控制（测试）

默认运行不会开启控制端点。测试脚本可以同时设置独立数据目录、独立偏好域、具名剪贴板和位于数据目录内的 Unix socket，从而驱动隔离实例而不触碰日常剪贴板与历史：

```bash
export CLIPSHELF_DATA_DIR=/tmp/clipshelf-test/data
export CLIPSHELF_DEFAULTS_SUITE=local.codex.ClipShelf.test
export CLIPSHELF_PASTEBOARD_NAME=local.codex.ClipShelf.test
export CLIPSHELF_CONTROL_SOCKET=/tmp/clipshelf-test/data/control.sock
.build/debug/ClipShelf &
.build/debug/ClipShelf --ctl ping
.build/debug/ClipShelf --ctl inject-text "hello"
.build/debug/ClipShelf --ctl status
.build/debug/ClipShelf --ctl quit
```

可用命令：`ping`、`status`、`export <路径>`、`inject-text <文字> [--type <类型>]`、`set-limit <数量>`、`clear`、`quit`。所有结果均以 JSON 输出。

### 生成下载包

```bash
./script/package_release.sh
```

生成的 zip 会放在 `release/` 目录里，可以上传到 GitHub Releases。

### 隐私说明

ClipShelf 的历史记录保存在本机：

```text
~/Library/Application Support/ClipShelf/history.json
```

项目没有服务器同步功能，剪贴板内容不会被上传。

### 首次打开提示

当前版本未进行 Apple 官方公证，下载后首次打开时 macOS 可能会提示“无法验证开发者”。

如果遇到这个提示，请在 Finder 中右键点击 `ClipShelf.app`，选择“打开”，然后在弹窗中再次确认“打开”。也可以进入“系统设置 → 隐私与安全性”，在安全提示处允许打开。

如果你介意未公证 App 的安全提示，也可以从源码自行构建。

### 协议

ClipShelf 使用 MIT License 开源。

---

## English

ClipShelf is a lightweight clipboard history app for macOS. It records text, files, and screenshots, and is especially useful if you want normal macOS screenshots to be copied to the clipboard while still keeping the original screenshot files.

### Features

- Records clipboard history for text, files, and images
- Excludes clipboard content marked as sensitive or transient, including entries from password managers
- Configurable history limit from 1–10,000 items, preserving pinned records first when trimming
- Watches a screenshot folder and automatically copies new screenshots to the clipboard
- Clearing history only removes ClipShelf records, not the original files in Finder or the screenshot folder
- Search, keyboard navigation, Enter to paste, and Space to preview
- Single selection, multi-selection, batch deletion, and Command-A/C/V support
- Customizable click behavior after multi-selection
- Three-finger drag multi-selection with an adjustable click recovery delay
- Pin records so important clips stay above normal history
- Custom global shortcut, clear-selection shortcut, and pin-selected-records shortcut
- Launch at login
- Four selectable app icon styles
- Shows an update button in the main window when a newer GitHub Release is available

### Requirements

- macOS 13 or later

### Setup Before Use

To make screenshots enter the clipboard immediately:

1. Set the macOS screenshot save location to the same folder selected in ClipShelf settings
2. Turn off “Show Floating Thumbnail” in the macOS screenshot tool

With this setup, screenshots are still saved as files while also being added to ClipShelf and copied to the clipboard.

### Run From Source

```bash
swift build
```

Build a local `.app` bundle:

```bash
./script/build_app_bundle.sh
```

Install to `~/Applications` and launch:

```bash
./script/install_app.sh
```

### Isolated Runtime Control (Testing)

The control endpoint is disabled by default. Test scripts can opt into an isolated data directory, preferences suite, named pasteboard, and Unix socket inside the data directory, allowing a second instance to be driven without touching the daily clipboard or history:

```bash
export CLIPSHELF_DATA_DIR=/tmp/clipshelf-test/data
export CLIPSHELF_DEFAULTS_SUITE=local.codex.ClipShelf.test
export CLIPSHELF_PASTEBOARD_NAME=local.codex.ClipShelf.test
export CLIPSHELF_CONTROL_SOCKET=/tmp/clipshelf-test/data/control.sock
.build/debug/ClipShelf &
.build/debug/ClipShelf --ctl ping
.build/debug/ClipShelf --ctl inject-text "hello"
.build/debug/ClipShelf --ctl status
.build/debug/ClipShelf --ctl quit
```

Available commands: `ping`, `status`, `export <path>`, `inject-text <text> [--type <type>]`, `set-limit <count>`, `clear`, and `quit`. Every response is JSON.

### Create a Download Package

```bash
./script/package_release.sh
```

The generated zip file will be placed in the `release/` directory and can be uploaded to GitHub Releases.

### Privacy

ClipShelf stores history locally:

```text
~/Library/Application Support/ClipShelf/history.json
```

There is no server-side sync, and clipboard contents are not uploaded.

### First Launch Notice

This free distribution build is not notarized by Apple. When opening the app for the first time, macOS may say it cannot verify the developer.

If that happens, right-click `ClipShelf.app` in Finder, choose “Open”, then confirm “Open” again. You can also allow the app from “System Settings → Privacy & Security”.

If you prefer to avoid warnings for non-notarized apps, you can build ClipShelf from source.

### License

ClipShelf is open-source under the MIT License.
