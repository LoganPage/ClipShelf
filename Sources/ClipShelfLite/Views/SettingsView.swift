import AppKit
import SwiftUI

struct SettingsView: View {
    @ObservedObject var store: ClipStore
    @ObservedObject var watcher: ScreenshotFolderWatcher
    var onClose: () -> Void = {}
    @State private var launchAtLogin = LoginItemController.isEnabled
    @State private var loginError: String?
    @State private var selectionColor = SelectionColorPreferences.color
    @State private var selectionColorPreset = SelectionColorPreferences.selectedPreset
    @State private var switchToClickedRecord = SelectionClickBehaviorPreferences.switchToClickedRecord
    @State private var multiSelectionClickSelectedBehavior = SelectionClickBehaviorPreferences.multiSelectionClickSelectedBehavior
    @State private var multiSelectionClickUnselectedBehavior = SelectionClickBehaviorPreferences.multiSelectionClickUnselectedBehavior
    @State private var clickRecoveryDuration = DragSelectionPreferences.clickRecoveryDuration
    @State private var hotKey = HotKeyDefaults.load()
    @State private var clearSelectionHotKey = ClearSelectionHotKeyDefaults.load()
    @State private var pinHotKey = PinHotKeyDefaults.load()
    @State private var appIconChoice = AppIconPreferences.selected
    @State private var hostWindow: NSWindow?
    @State private var appearanceMode = AppearancePreferences.mode
    @State private var systemColorScheme = AppearancePreferences.systemColorScheme

    private var resolvedColorScheme: ColorScheme {
        appearanceMode == .system ? systemColorScheme : (appearanceMode.colorScheme ?? .light)
    }

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    appearanceSection
                    iconSection
                    screenshotSection
                    historySection
                    hotKeySection
                    launchSection
                }
                .padding(22)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .background(AutoHidingScrollViewConfigurator())

            Divider()

            HStack {
                Spacer()
                Button("完成") {
                    onClose()
                }
                .keyboardShortcut(.defaultAction)
            }
            .padding(.horizontal, 22)
            .padding(.vertical, 14)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(AppTheme.settingsBackground)
        .preferredColorScheme(resolvedColorScheme)
        .background(WindowReader { window in
            hostWindow = window
        })
        .onReceive(NotificationCenter.default.publisher(for: HotKeyDefaults.changedNotification)) { notification in
            hotKey = notification.object as? HotKeyConfiguration ?? HotKeyDefaults.load()
        }
        .onReceive(NotificationCenter.default.publisher(for: ClearSelectionHotKeyDefaults.changedNotification)) { notification in
            clearSelectionHotKey = notification.object as? HotKeyConfiguration ?? ClearSelectionHotKeyDefaults.load()
        }
        .onReceive(NotificationCenter.default.publisher(for: PinHotKeyDefaults.changedNotification)) { notification in
            pinHotKey = notification.object as? HotKeyConfiguration ?? PinHotKeyDefaults.load()
        }
        .onReceive(NotificationCenter.default.publisher(for: AppIconPreferences.changedNotification)) { notification in
            appIconChoice = notification.object as? AppIconChoice ?? AppIconPreferences.selected
        }
        .onReceive(NotificationCenter.default.publisher(for: AppearancePreferences.changedNotification)) { notification in
            appearanceMode = notification.object as? AppearanceMode ?? AppearancePreferences.mode
            systemColorScheme = AppearancePreferences.systemColorScheme
        }
        .onReceive(NotificationCenter.default.publisher(for: AppearancePreferences.systemChangedNotification)) { _ in
            systemColorScheme = AppearancePreferences.systemColorScheme
        }
        .onReceive(NotificationCenter.default.publisher(for: SelectionColorPreferences.changedNotification)) { _ in
            selectionColor = SelectionColorPreferences.color
            selectionColorPreset = SelectionColorPreferences.selectedPreset
        }
        .onReceive(NotificationCenter.default.publisher(for: SelectionClickBehaviorPreferences.changedNotification)) { notification in
            switchToClickedRecord = notification.object as? Bool ?? SelectionClickBehaviorPreferences.switchToClickedRecord
            multiSelectionClickSelectedBehavior = SelectionClickBehaviorPreferences.multiSelectionClickSelectedBehavior
            multiSelectionClickUnselectedBehavior = SelectionClickBehaviorPreferences.multiSelectionClickUnselectedBehavior
        }
        .onReceive(NotificationCenter.default.publisher(for: DragSelectionPreferences.changedNotification)) { notification in
            clickRecoveryDuration = notification.object as? TimeInterval ?? DragSelectionPreferences.clickRecoveryDuration
        }
    }

    private var appearanceSection: some View {
        settingsSection("暗黑模式") {
            Picker("暗黑模式", selection: Binding(
                get: { appearanceMode },
                set: updateAppearanceMode
            )) {
                ForEach(AppearanceMode.allCases) { mode in
                    Text(mode.title).tag(mode)
                }
            }
            .pickerStyle(.segmented)

            Text("深色模式使用石墨背景和分层表面，类型颜色在暗色背景上保持清晰。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var iconSection: some View {
        settingsSection("图标") {
            HStack(spacing: 14) {
                ForEach(AppIconChoice.allCases) { choice in
                    iconChoiceButton(choice)
                }
            }

            Text("选择后会立即更新应用内图标预览，并在下次打开时继续使用。程序坞图标固定使用 AppIcon，避免运行时大小变化。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var screenshotSection: some View {
        settingsSection("截图") {
            HStack {
                Text("截图文件夹")
                Spacer()
                Text(watcher.folderURL?.path ?? "未选择")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            HStack {
                Button("选择文件夹") {
                    debugFolderPickerLog("choose folder button clicked")
                    watcher.chooseFolder(attachedTo: hostWindow)
                }

                Button("在访达中显示") {
                    watcher.revealFolder()
                }
                .disabled(watcher.folderURL == nil)
            }

            Text("请把 macOS 截图设置里的保存位置设成同一个文件夹，并关闭右下角浮动缩略图。新截图会保留原文件，同时自动进入剪贴板。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var historySection: some View {
        settingsSection("历史") {
            Toggle("记录文字、文件和图片复制历史", isOn: $store.isClipboardHistoryEnabled)

            Toggle("单选后点击其它记录改选该记录", isOn: Binding(
                get: { switchToClickedRecord },
                set: updateSelectionClickBehavior
            ))

            HStack {
                Text("多选后点击已选记录")
                Spacer()
                Picker("", selection: Binding(
                    get: { multiSelectionClickSelectedBehavior },
                    set: updateMultiSelectionClickSelectedBehavior
                )) {
                    ForEach(MultiSelectionClickSelectedBehavior.allCases) { behavior in
                        Text(behavior.title).tag(behavior)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
            }

            HStack {
                Text("多选后点击未选记录")
                Spacer()
                Picker("", selection: Binding(
                    get: { multiSelectionClickUnselectedBehavior },
                    set: updateMultiSelectionClickUnselectedBehavior
                )) {
                    ForEach(MultiSelectionClickUnselectedBehavior.allCases) { behavior in
                        Text(behavior.title).tag(behavior)
                    }
                }
                .labelsHidden()
                .pickerStyle(.menu)
            }

            Text("单选点击自身会取消选择。多选点击已选记录和未选记录可分别设置。")
                .font(.caption)
                .foregroundStyle(.secondary)

            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("点击恢复期")
                    Spacer()
                    Text(String(format: "%.1f 秒", clickRecoveryDuration))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Slider(
                    value: Binding(
                        get: { clickRecoveryDuration },
                        set: updateClickRecoveryDuration
                    ),
                    in: 0...1,
                    step: 0.1
                )

                Text("三指拖移松手后，短时间内的下一次点击会走恢复逻辑。设为 0 秒时几乎直接使用普通点击逻辑。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            VStack(alignment: .leading, spacing: 8) {
                Text("选中记录底色")
                    .font(.system(size: 13, weight: .medium))

                HStack(spacing: 8) {
                    ForEach(SelectionColorPreset.allCases) { preset in
                        selectionColorPresetButton(preset)
                    }
                }

                HStack(spacing: 12) {
                    ColorPicker("自定义颜色", selection: Binding(
                        get: { selectionColor },
                        set: updateSelectionColor
                    ), supportsOpacity: false)

                    Text(selectionColorPreset == nil ? "当前使用自定义颜色" : "预设会随浅色/深色模式自动切换")
                        .font(.caption)
                        .foregroundStyle(.secondary)

                    Spacer()

                    Button("恢复默认") {
                        SelectionColorPreferences.reset()
                        selectionColorPreset = SelectionColorPreferences.selectedPreset
                        selectionColor = SelectionColorPreferences.color
                    }
                }

                Text("记录被选中时只改变底色，文字保持黑色。")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Button("查看历史存储位置") {
                store.revealStorage()
            }

            Button("清空历史", role: .destructive) {
                store.clearHistory()
            }

            Text("清空历史只删除 ClipShelf 的记录，不删除截图文件夹或访达里的原文件。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var hotKeySection: some View {
        settingsSection("快捷键") {
            HStack {
                Text("全局快捷键呼出")
                Spacer()
                HotKeyRecorderView(configuration: $hotKey)
            }

            HStack {
                Text("取消选择记录")
                Spacer()
                HotKeyRecorderView(
                    configuration: $clearSelectionHotKey,
                    save: ClearSelectionHotKeyDefaults.save,
                    pausesGlobalHotKey: false
                )
            }

            HStack {
                Text("置顶选中记录")
                Spacer()
                HotKeyRecorderView(
                    configuration: $pinHotKey,
                    save: PinHotKeyDefaults.save,
                    pausesGlobalHotKey: false
                )
            }

            Text("点击快捷键按钮后按新的组合键。按 Esc 取消录入。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var launchSection: some View {
        settingsSection("启动") {
            Toggle("开机自启动", isOn: Binding(
                get: { launchAtLogin },
                set: updateLaunchAtLogin
            ))

            if let loginError {
                Text(loginError)
                    .font(.caption)
                    .foregroundStyle(.red)
            }
        }
    }

    private func settingsSection<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title)
                .font(.system(size: 16, weight: .semibold))

            content()
        }
    }

    private func updateLaunchAtLogin(_ enabled: Bool) {
        do {
            try LoginItemController.setEnabled(enabled)
            launchAtLogin = LoginItemController.isEnabled
            loginError = nil
        } catch {
            launchAtLogin = LoginItemController.isEnabled
            loginError = error.localizedDescription
        }
    }

    private func updateSelectionColor(_ color: Color) {
        selectionColor = color
        selectionColorPreset = nil
        SelectionColorPreferences.color = color
    }

    private func selectSelectionColorPreset(_ preset: SelectionColorPreset) {
        selectionColorPreset = preset
        selectionColor = preset.color
        SelectionColorPreferences.select(preset)
    }

    private func updateSelectionClickBehavior(_ enabled: Bool) {
        switchToClickedRecord = enabled
        SelectionClickBehaviorPreferences.switchToClickedRecord = enabled
    }

    private func updateMultiSelectionClickSelectedBehavior(_ behavior: MultiSelectionClickSelectedBehavior) {
        multiSelectionClickSelectedBehavior = behavior
        SelectionClickBehaviorPreferences.multiSelectionClickSelectedBehavior = behavior
    }

    private func updateMultiSelectionClickUnselectedBehavior(_ behavior: MultiSelectionClickUnselectedBehavior) {
        multiSelectionClickUnselectedBehavior = behavior
        SelectionClickBehaviorPreferences.multiSelectionClickUnselectedBehavior = behavior
    }

    private func updateClickRecoveryDuration(_ duration: TimeInterval) {
        clickRecoveryDuration = duration
        DragSelectionPreferences.clickRecoveryDuration = duration
    }

    private func updateAppIcon(_ choice: AppIconChoice) {
        appIconChoice = choice
        AppIconPreferences.selected = choice
    }

    private func updateAppearanceMode(_ mode: AppearanceMode) {
        appearanceMode = mode
        AppearancePreferences.mode = mode
    }

    private func selectionColorPresetButton(_ preset: SelectionColorPreset) -> some View {
        Button {
            selectSelectionColorPreset(preset)
        } label: {
            VStack(spacing: 5) {
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(preset.color)
                    .frame(maxWidth: .infinity)
                    .frame(height: 30)
                    .overlay {
                        if selectionColorPreset == preset {
                            Image(systemName: "checkmark")
                                .font(.system(size: 12, weight: .bold))
                                .foregroundStyle(Color(nsColor: .labelColor))
                        }
                    }
                    .overlay {
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .stroke(selectionColorPreset == preset ? Color.accentColor : AppTheme.subtleBorder, lineWidth: selectionColorPreset == preset ? 2 : 1)
                    }

                Text(preset.title)
                    .font(.caption2)
                    .foregroundStyle(.primary)
                    .lineLimit(1)
            }
        }
        .buttonStyle(.plain)
        .help(preset.feeling)
    }

    @ViewBuilder
    private func iconChoiceButton(_ choice: AppIconChoice) -> some View {
        Button {
            updateAppIcon(choice)
        } label: {
            VStack(spacing: 8) {
                ZStack {
                    RoundedRectangle(cornerRadius: 16)
                        .fill(AppTheme.rowBackground)
                    RoundedRectangle(cornerRadius: 16)
                        .stroke(appIconChoice == choice ? Color.accentColor : Color.secondary.opacity(0.18), lineWidth: appIconChoice == choice ? 2 : 1)

                    if let image = choice.previewImage {
                        Image(nsImage: image)
                            .resizable()
                            .scaledToFit()
                            .padding(12)
                    } else {
                        Image(systemName: "app")
                            .font(.system(size: 30))
                            .foregroundStyle(.secondary)
                    }
                }
                .frame(width: 112, height: 112)

                Text(choice.title)
                    .font(.caption)
                    .foregroundStyle(.primary)
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel("选择\(choice.title)")
    }
}

private struct AutoHidingScrollViewConfigurator: NSViewRepresentable {
    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        configureSoon(from: view, coordinator: context.coordinator)
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        configureSoon(from: nsView, coordinator: context.coordinator)
    }

    private func configureSoon(from view: NSView, coordinator: Coordinator) {
        DispatchQueue.main.async {
            guard let scrollView = resolveSettingsScrollView(from: view) else { return }
            coordinator.configure(scrollView)
        }
    }

    private func resolveSettingsScrollView(from view: NSView) -> NSScrollView? {
        if let enclosing = view.enclosingScrollView {
            return enclosing
        }

        guard let contentView = view.window?.contentView else { return nil }
        let candidates = allScrollViews(in: contentView)
            .filter { $0.bounds.width > 120 && $0.bounds.height > 120 }
        guard !candidates.isEmpty else { return nil }

        return candidates.min { lhs, rhs in
            lhs.bounds.width * lhs.bounds.height < rhs.bounds.width * rhs.bounds.height
        }
    }

    private func allScrollViews(in view: NSView) -> [NSScrollView] {
        var result: [NSScrollView] = []
        if let scrollView = view as? NSScrollView {
            result.append(scrollView)
        }

        for subview in view.subviews {
            result.append(contentsOf: allScrollViews(in: subview))
        }

        return result
    }

    final class Coordinator {
        private weak var scrollView: NSScrollView?
        private var monitor: Any?
        private var hideWorkItem: DispatchWorkItem?

        deinit {
            if let monitor {
                NSEvent.removeMonitor(monitor)
            }
        }

        func configure(_ scrollView: NSScrollView) {
            guard self.scrollView !== scrollView else { return }
            self.scrollView = scrollView
            scrollView.scrollerStyle = .overlay
            scrollView.autohidesScrollers = true
            scrollView.hasVerticalScroller = true
            scrollView.verticalScroller?.isHidden = true
            scrollView.verticalScroller?.alphaValue = 0
            installMonitorIfNeeded()
        }

        private func installMonitorIfNeeded() {
            guard monitor == nil else { return }
            monitor = NSEvent.addLocalMonitorForEvents(matching: .scrollWheel) { [weak self] event in
                guard let self,
                      let scrollView,
                      event.window === scrollView.window else {
                    return event
                }

                let point = scrollView.convert(event.locationInWindow, from: nil)
                if scrollView.bounds.contains(point) {
                    showScrollerBriefly(scrollView)
                }

                return event
            }
        }

        private func showScrollerBriefly(_ scrollView: NSScrollView) {
            scrollView.hasVerticalScroller = true
            scrollView.verticalScroller?.isHidden = false
            scrollView.verticalScroller?.alphaValue = 1
            scrollView.reflectScrolledClipView(scrollView.contentView)

            hideWorkItem?.cancel()
            let workItem = DispatchWorkItem { [weak scrollView] in
                scrollView?.verticalScroller?.alphaValue = 0
                scrollView?.verticalScroller?.isHidden = true
            }
            hideWorkItem = workItem
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.9, execute: workItem)
        }
    }
}
