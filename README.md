# ClipShelf

> Windows 体验版已开源：[Windows 1.0.22 源码、构建说明与已知限制](Windows/README.md)。下方说明仍针对 macOS。


## 中文

ClipShelf 是一个简洁的 macOS 剪贴板历史工具，支持文字、文件和截图记录。它适合想让普通 macOS 截图自动进入剪贴板，同时继续保留原截图文件的人。

### 功能

- 记录文字、文件和图片剪贴板历史
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

