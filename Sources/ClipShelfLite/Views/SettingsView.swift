import AppKit
import SwiftUI

struct SettingsView: View {
    @ObservedObject var store: ClipStore
    @ObservedObject var watcher: ScreenshotFolderWatcher
    var onClose: () -> Void = {}
    @State private var launchAtLogin = LoginItemController.isEnabled
    @State private var loginError: String?
    @State private var switchToClickedRecord = SelectionClickBehaviorPreferences.switchToClickedRecord
    @State private var multiSelectionClickSelectedBehavior = SelectionClickBehaviorPreferences.multiSelectionClickSelectedBehavior
    @State private var multiSelectionClickUnselectedBehavior = SelectionClickBehaviorPreferences.multiSelectionClickUnselectedBehavior
    @State private var hotKey = HotKeyDefaults.load()
    @State private var clearSelectionHotKey = ClearSelectionHotKeyDefaults.load()
    @State private var pinHotKey = PinHotKeyDefaults.load()
    @State private var closeSettingsHotKey = CloseSettingsHotKeyDefaults.load()
    @State private var toggleSearchHotKey = ToggleSearchHotKeyDefaults.load()
    @State private var hostWindow: NSWindow?
    @State private var appearanceMode = AppearancePreferences.mode
    @State private var glassTransparency = GlassPreferences.value
    @State private var scrollEffect = ScrollEffectPreferences.value
    @State private var listEntrance = ListEntrancePreferences.value
    @State private var systemColorScheme = AppearancePreferences.systemColorScheme
    @State private var historyLimitText = ""
    @State private var oldHistoryRetentionDays = OldHistoryCleanup.retentionDays
    @State private var oldHistoryStatusText = ""
    @State private var oldHistoryEligibleCount = 0
    @State private var showingClearHistoryConfirmation = false
    @FocusState private var historyLimitIsFocused: Bool

    /// The 设置 capsule's height and the ✓ circle's diameter.
    static let barButtonSize: CGFloat = 40
    /// Gap between those buttons and the panel's top / bottom edge.
    static let barInset: CGFloat = 10

    private var resolvedColorScheme: ColorScheme {
        appearanceMode == .system ? systemColorScheme : (appearanceMode.colorScheme ?? .light)
    }

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                // Built in full before the panel fades in: lazily adding sections
                // re-measured everything above them and stuttered while visible,
                // and lazy stacks stalled mid-scroll to build each section.
                VStack(alignment: .leading, spacing: 22) {
                    appearanceSection
                    glassSection
                    scrollEffectSection
                    screenshotSection
                    historySection
                    selectionSection
                    oldHistorySection
                    hotKeySection
                    launchSection
                }
                .padding(.horizontal, 22)
                // Clears the 设置 capsule (the header inset ends at its bottom edge).
                .padding(.top, 18)
                // The last card can scroll clear of the ✓ in the corner.
                .padding(.bottom, Self.barButtonSize + Self.barInset + 18)
                .frame(maxWidth: .infinity, alignment: .leading)
                .buttonStyle(LiquidGlassButtonStyle())
                // All cards' glass in one system container.
                .glassGroup()
            }
            // The title and ✓ float over the content as glass, and content
            // softly blurs away under them and at the bottom edge.
            .safeAreaInset(edge: .top, spacing: 0) {
                HStack {
                    Text("设置")
                        .font(.system(size: 17, weight: .semibold))
                        .padding(.horizontal, 18)
                        .frame(height: Self.barButtonSize)
                        .clipShelfGlass(in: Capsule())
                    Spacer()
                }
                .padding(.horizontal, 22)
                .padding(.top, Self.barInset)
                // Frosts what scrolls under the header: exactly from the top
                // of the panel to the bottom edge of the 设置 capsule.
                .background { EdgeBlurBand(edge: .top) }
            }
            .background(OverlayScrollerConfigurator())
            // The same pairing at the bottom: ✓ in the corner, and a band
            // from the bottom edge up to exactly the top of the ✓ circle.
            .overlay(alignment: .bottom) {
                HStack {
                    Spacer()
                    DoneGlassButton(size: Self.barButtonSize) {
                        commitHistoryLimit()
                        onClose()
                    }
                }
                .padding(.horizontal, 22)
                .padding(.bottom, Self.barInset)
                .background { EdgeBlurBand(edge: .bottom) }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .coordinateSpace(name: SettingsPanelSpace.name)
        .background {
            // On macOS 26 the hosting panel is Liquid Glass; keep it visible.
            if #unavailable(macOS 26.0) {
                AppTheme.settingsBackground
            }
        }
        .preferredColorScheme(resolvedColorScheme)
        .background(WindowReader { window in
            hostWindow = window
        })
        .confirmationDialog(
            ClearHistoryConfirmation.title(itemCount: store.items.count),
            isPresented: $showingClearHistoryConfirmation,
            titleVisibility: .visible
        ) {
            Button("清空", role: .destructive) {
                store.clearHistory()
            }
            Button("取消", role: .cancel) { }
        } message: {
            Text(ClearHistoryConfirmation.message(itemCount: store.items.count))
        }
        // The slider and its sample follow the saved level, also when it
        // changes from elsewhere.
        .onReceive(NotificationCenter.default.publisher(for: GlassPreferences.changedNotification)) { notification in
            let value = notification.object as? GlassTransparency ?? GlassPreferences.value
            glassTransparency = value
            glassPreview.level = value.level
        }
        .onReceive(NotificationCenter.default.publisher(for: HotKeyDefaults.changedNotification)) { notification in
            hotKey = notification.object as? HotKeyConfiguration ?? HotKeyDefaults.load()
        }
        .onReceive(NotificationCenter.default.publisher(for: ClearSelectionHotKeyDefaults.changedNotification)) { notification in
            clearSelectionHotKey = notification.object as? HotKeyConfiguration ?? ClearSelectionHotKeyDefaults.load()
        }
        .onReceive(NotificationCenter.default.publisher(for: PinHotKeyDefaults.changedNotification)) { notification in
            pinHotKey = notification.object as? HotKeyConfiguration ?? PinHotKeyDefaults.load()
        }
        .onReceive(NotificationCenter.default.publisher(for: AppearancePreferences.changedNotification)) { notification in
            appearanceMode = notification.object as? AppearanceMode ?? AppearancePreferences.mode
            systemColorScheme = AppearancePreferences.systemColorScheme
        }
        .onReceive(NotificationCenter.default.publisher(for: AppearancePreferences.systemChangedNotification)) { _ in
            systemColorScheme = AppearancePreferences.systemColorScheme
        }
        .onReceive(NotificationCenter.default.publisher(for: SelectionClickBehaviorPreferences.changedNotification)) { notification in
            switchToClickedRecord = notification.object as? Bool ?? SelectionClickBehaviorPreferences.switchToClickedRecord
            multiSelectionClickSelectedBehavior = SelectionClickBehaviorPreferences.multiSelectionClickSelectedBehavior
            multiSelectionClickUnselectedBehavior = SelectionClickBehaviorPreferences.multiSelectionClickUnselectedBehavior
        }
        .onAppear {
            reloadDisplayedValues()
        }
        // The window is reused, so refresh what it shows each time it opens,
        // and save the history limit field however it is closed.
        .onReceive(NotificationCenter.default.publisher(for: SettingsWindowController.willShowNotification)) { _ in
            reloadDisplayedValues()
        }
        .onReceive(NotificationCenter.default.publisher(for: SettingsWindowController.willCloseNotification)) { _ in
            commitHistoryLimit()
        }
    }

    private var appearanceSection: some View {
        settingsSection("外观") {
            // Label left, control right, like every other row.
            HStack {
                Text("外观模式")
                Spacer()
                LiquidSegmentedControl(
                    options: AppearanceMode.allCases,
                    selection: Binding(
                        get: { appearanceMode },
                        set: updateAppearanceMode
                    ),
                    title: \.title,
                    segmentWidth: 84,
                    height: SettingsControlMetrics.height
                )
                .accessibilityLabel("外观")
            }

            settingsCaption("跟随系统会随 macOS 的浅色 / 深色外观自动切换。")
        }
    }

    private var scrollEffectSection: some View {
        settingsSection("列表") {
            HStack {
                Text("滚动效果")
                Spacer()
                LiquidMenu(
                    options: ScrollEffectStyle.allCases,
                    selection: Binding(
                        get: { scrollEffect },
                        set: { value in
                            scrollEffect = value
                            ScrollEffectPreferences.value = value
                        }
                    ),
                    title: \.title
                )
                .accessibilityLabel("滚动效果")
            }

            settingsCaption("渐进模糊：记录滑到顶部工具栏下或滑出底部时，随滑入的程度逐渐模糊。")

            HStack {
                Text("切换动画")
                Spacer()
                LiquidMenu(
                    options: ListEntranceStyle.allCases,
                    selection: Binding(
                        get: { listEntrance },
                        set: { value in
                            listEntrance = value
                            ListEntrancePreferences.value = value
                        }
                    ),
                    title: \.title
                )
                .accessibilityLabel("切换动画")
            }

            settingsCaption("切换记录类型时，新记录出现的方式。")
        }
    }

    /// Watched only by the sample, so dragging never re-renders the page.
    @StateObject private var glassPreview = GlassPreviewModel(level: GlassPreferences.value.level)

    private var glassSection: some View {
        settingsSection("玻璃效果") {
            // Like Apple's appearance preview: while dragging only this
            // sample is re-rendered; the whole interface follows on release.
            GlassPreviewSample(model: glassPreview)

            GlassLevelSlider(
                value: Binding(
                    get: { glassTransparency.level },
                    set: { glassTransparency = GlassTransparency(level: $0) }
                ),
                onPreview: { glassPreview.level = $0 },
                onCommit: {
                    glassPreview.level = $0
                    GlassPreferences.value = GlassTransparency(level: $0)
                }
            )

            settingsCaption("往左更清透，往右玻璃着色更重、更易读。拖动时上方样例实时变化，松手后整个界面再跟上。")

        }
    }

    private var screenshotSection: some View {
        settingsSection("截图与图片") {
            HStack {
                Text("监听的文件夹")
                Spacer()
                Text("\(watcher.folderURLs.count) / \(ScreenshotFolderWatcher.maximumFolders)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            ForEach(Array(watcher.folderURLs.enumerated()), id: \.offset) { index, url in
                VStack(alignment: .leading, spacing: 10) {
                    HStack(spacing: 8) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(FileManager.default.displayName(atPath: url.path))
                            Text(url.path)
                                .font(.caption)
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                                .truncationMode(.middle)
                        }
                        .help(url.path)
                        Spacer(minLength: 8)
                        Button("更换") {
                            watcher.chooseFolder(attachedTo: hostWindow, replacing: index)
                        }
                        Button("访达") {
                            watcher.revealFolder(at: index)
                        }
                        Button {
                            watcher.removeFolder(at: index)
                        } label: {
                            Image(systemName: "minus")
                                .font(.system(size: 11, weight: .bold))
                        }
                        .help("不再监听这个文件夹")
                        .accessibilityLabel("移除文件夹")
                    }
                    folderRuleRows(for: url)
                }
                .padding(12)
                .background(
                    RoundedRectangle(cornerRadius: 14, style: .continuous)
                        .fill(Color.primary.opacity(0.05))
                )
            }

            if watcher.folderURLs.isEmpty {
                Text("未选择")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            HStack {
                Button(watcher.folderURLs.isEmpty ? "选择文件夹" : "添加文件夹") {
                    debugFolderPickerLog("choose folder button clicked")
                    watcher.chooseFolder(attachedTo: hostWindow)
                }
                .disabled(!watcher.canAddFolder)
                if !watcher.canAddFolder {
                    Text("最多同时监听 \(ScreenshotFolderWatcher.maximumFolders) 个文件夹")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Text("每个文件夹可以分别设置收什么、要不要自动复制、是否包括子文件夹。原文件都会保留；正在下载的文件等下载完才收，同一个文件再下载一次不会重复记录。截图时请关闭右下角浮动缩略图。")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private func folderRuleRows(for url: URL) -> some View {
        let rule = watcher.rule(for: url)
        if let problem = watcher.status(for: url).problem {
            Label(problem, systemImage: "exclamationmark.triangle.fill")
                .font(.caption)
                .foregroundStyle(.red)
                .fixedSize(horizontal: false, vertical: true)
        }
        HStack {
            Text("收进来")
            Spacer()
            LiquidMenu(
                options: WatchedFolderRule.Content.allCases,
                selection: Binding(
                    get: { rule.content },
                    set: { content in
                        var updated = watcher.rule(for: url)
                        updated.content = content
                        watcher.setRule(updated, for: url)
                    }
                ),
                title: \.title
            )
            .accessibilityLabel("收进来的内容")
        }
        settingsToggle("自动复制到剪贴板", isOn: Binding(
            get: { rule.copiesToClipboard },
            set: { copies in
                var updated = watcher.rule(for: url)
                updated.copiesToClipboard = copies
                watcher.setRule(updated, for: url)
            }
        ))
        settingsToggle("包括子文件夹", isOn: Binding(
            get: { rule.includesSubfolders },
            set: { includes in
                var updated = watcher.rule(for: url)
                updated.includesSubfolders = includes
                watcher.setRule(updated, for: url)
            }
        ))
    }

    private var historySection: some View {
        settingsSection("历史") {
            settingsToggle("记录文字、文件和图片的复制历史", isOn: $store.isClipboardHistoryEnabled)

            HStack {
                Text("最多保留记录")
                Spacer()
                LiquidStepper(
                    text: $historyLimitText,
                    onIncrement: { updateHistoryLimit(min(store.maxItems + 1, HistoryLimitPreferences.allowedRange.upperBound)) },
                    onDecrement: { updateHistoryLimit(max(store.maxItems - 1, HistoryLimitPreferences.allowedRange.lowerBound)) },
                    onCommit: commitHistoryLimit,
                    focus: $historyLimitIsFocused
                )
                .onChange(of: historyLimitIsFocused) { isFocused in
                    if !isFocused {
                        commitHistoryLimit()
                    }
                }
            }

            settingsCaption("可设置 1–10000 条。调低后会立即删除最旧的未置顶记录，只影响 ClipShelf 历史，不删除任何原文件。")

            HStack(spacing: 10) {
                Button("查看历史存储位置") {
                    store.revealStorage()
                }

                Button("清空历史…", role: .destructive) {
                    guard ClearHistoryConfirmation.shouldConfirm(itemCount: store.items.count) else { return }
                    showingClearHistoryConfirmation = true
                }
                .foregroundStyle(.red)
            }

            settingsCaption("清空历史只删除 ClipShelf 的记录，不删除截图文件夹或访达里的原文件。")
        }
    }

    private var selectionSection: some View {
        settingsSection("选择") {
            settingsToggle("单选后点击其它记录，直接改选该记录", isOn: Binding(
                get: { switchToClickedRecord },
                set: updateSelectionClickBehavior
            ))

            HStack {
                Text("多选后点击已选记录")
                Spacer()
                LiquidMenu(
                    options: MultiSelectionClickSelectedBehavior.allCases,
                    selection: Binding(
                        get: { multiSelectionClickSelectedBehavior },
                        set: updateMultiSelectionClickSelectedBehavior
                    ),
                    title: \.title
                )
            }

            HStack {
                Text("多选后点击未选记录")
                Spacer()
                LiquidMenu(
                    options: MultiSelectionClickUnselectedBehavior.allCases,
                    selection: Binding(
                        get: { multiSelectionClickUnselectedBehavior },
                        set: updateMultiSelectionClickUnselectedBehavior
                    ),
                    title: \.title
                )
            }

            settingsCaption("单选时再点一次自身会取消选择。多选时点击已选、未选记录的行为可以分别设置。")
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

            HStack {
                Text("呼出 / 关闭搜索框")
                Spacer()
                HotKeyRecorderView(
                    configuration: $toggleSearchHotKey,
                    save: ToggleSearchHotKeyDefaults.save,
                    pausesGlobalHotKey: false
                )
            }

            HStack {
                Text("关闭设置")
                Spacer()
                HotKeyRecorderView(
                    configuration: $closeSettingsHotKey,
                    save: CloseSettingsHotKeyDefaults.save,
                    pausesGlobalHotKey: false
                )
            }

            HStack(alignment: .center) {
                settingsCaption("点击快捷键按钮后按新的组合键，按 Esc 取消录入。设置窗口也可以用 ⌘, 或 Esc 打开 / 关闭。")
                Spacer(minLength: 12)
                Button("恢复默认") {
                    restoreDefaultHotKeys()
                }
            }
        }
    }

    /// Puts every shortcut back to its default; each capsule springs to
    /// its new width.
    private func restoreDefaultHotKeys() {
        withAnimation(.clipShelf(.spring(response: 0.42, dampingFraction: 0.6))) {
            hotKey = .defaultValue
            clearSelectionHotKey = .defaultClearSelectionValue
            pinHotKey = .defaultPinValue
            toggleSearchHotKey = .defaultToggleSearchValue
            closeSettingsHotKey = .defaultCloseSettingsValue
        }
        HotKeyDefaults.save(.defaultValue)
        HotKeyManager.shared.register(configuration: .defaultValue)
        ClearSelectionHotKeyDefaults.save(.defaultClearSelectionValue)
        PinHotKeyDefaults.save(.defaultPinValue)
        ToggleSearchHotKeyDefaults.save(.defaultToggleSearchValue)
        CloseSettingsHotKeyDefaults.save(.defaultCloseSettingsValue)
    }

    private var oldHistorySection: some View {
        settingsSection("清理旧历史") {
            HStack {
                Text("保留最近")
                Spacer()
                LiquidMenu(
                    options: OldHistoryCleanup.retentionOptions,
                    selection: Binding(
                        get: { oldHistoryRetentionDays },
                        set: updateOldHistoryRetentionDays
                    ),
                    title: { "\($0) 天" }
                )
            }

            HStack(spacing: 10) {
                Button("查看待清理记录数量") {
                    refreshOldHistoryCleanupSummary()
                }

                Button("清理这些记录", role: .destructive) {
                    confirmOldHistoryCleanup()
                }
                .foregroundStyle(oldHistoryEligibleCount == 0 ? Color.secondary : Color.red)
                .disabled(oldHistoryEligibleCount == 0)
            }

            if !oldHistoryStatusText.isEmpty {
                settingsCaption(oldHistoryStatusText)
            }

            settingsCaption("只移除所选天数之前的未置顶记录，不删除原文件。清理后仍可按 ⌘Z 撤销。")
        }
    }

    private var launchSection: some View {
        settingsSection("启动") {
            settingsToggle("开机时自动启动 ClipShelf", isOn: Binding(
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

    /// A row with its label on the left and a switch on the right, like System Settings.
    private func settingsToggle(_ title: String, isOn: Binding<Bool>) -> some View {
        HStack {
            Text(title)
            Spacer()
            LiquidToggle(isOn: isOn)
                .accessibilityLabel(title)
        }
    }

    private func settingsCaption(_ text: String) -> some View {
        Text(text)
            .font(.caption)
            // A notch stronger than .secondary: on dark glass that read too faint.
            .foregroundStyle(Color.primary.opacity(resolvedColorScheme == .dark ? 0.68 : 0.58))
            .fixedSize(horizontal: false, vertical: true)
    }

    private func settingsSection<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        // The title sits inside the frosted card, not on the clear panel
        // glass, so it stays readable whatever is behind the window.
        VStack(alignment: .leading, spacing: 12) {
            // Larger and bold, in the full label colour, so each card's
            // title stands out from its rows.
            Text(title)
                .font(.system(size: 16, weight: .bold))
                .foregroundStyle(Color.primary)

            content()
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .cardGlass()
    }

    private func reloadDisplayedValues() {
        historyLimitText = String(store.maxItems)
        oldHistoryRetentionDays = OldHistoryCleanup.retentionDays
        launchAtLogin = LoginItemController.isEnabled
        loginError = nil
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

    private func updateHistoryLimit(_ value: Int) {
        // Runs on every close too: touching the store when nothing changed
        // re-rendered the whole list and rewrote history mid-animation.
        if value != store.maxItems {
            store.setHistoryLimit(value)
        }
        let text = String(store.maxItems)
        if historyLimitText != text {
            historyLimitText = text
        }
    }

    private func updateOldHistoryRetentionDays(_ days: Int) {
        oldHistoryRetentionDays = OldHistoryCleanup.save(days, to: AppEnvironment.userDefaults)
        oldHistoryEligibleCount = 0
        oldHistoryStatusText = ""
    }

    private func refreshOldHistoryCleanupSummary() {
        oldHistoryEligibleCount = OldHistoryCleanup.eligibleIDs(
            in: store.items,
            now: Date(),
            retentionDays: oldHistoryRetentionDays
        ).count
        oldHistoryStatusText = OldHistoryCleanup.summaryText(
            eligibleCount: oldHistoryEligibleCount,
            retentionDays: oldHistoryRetentionDays
        )
    }

    private func confirmOldHistoryCleanup() {
        let removedCount = store.removeOldUnpinnedItems(retentionDays: oldHistoryRetentionDays)
        oldHistoryEligibleCount = 0
        oldHistoryStatusText = removedCount > 0
            ? "已清理 \(removedCount) 条记录。"
            : OldHistoryCleanup.summaryText(eligibleCount: 0, retentionDays: oldHistoryRetentionDays)
    }

    private func commitHistoryLimit() {
        let normalizedValue = HistoryLimitPreferences.normalized(
            text: historyLimitText,
            fallback: store.maxItems
        )
        updateHistoryLimit(normalizedValue)
    }

    private func updateAppearanceMode(_ mode: AppearanceMode) {
        appearanceMode = mode
        AppearancePreferences.mode = mode
    }

}

/// Uses the system overlay scroller, which already fades in while scrolling
/// and hides when idle. Configured once; no per-scroll-event work.
private struct OverlayScrollerConfigurator: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        DispatchQueue.main.async {
            guard let scrollView = Self.settingsScrollView(near: view) else { return }
            scrollView.scrollerStyle = .overlay
            scrollView.autohidesScrollers = true
            scrollView.hasVerticalScroller = true
            GlassScroller.install(in: scrollView)
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}

    private static func settingsScrollView(near view: NSView) -> NSScrollView? {
        if let enclosing = view.enclosingScrollView {
            return enclosing
        }
        guard let contentView = view.window?.contentView else { return nil }
        // The settings scroll view is the smallest sizeable one in the window.
        return allScrollViews(in: contentView)
            .filter { $0.bounds.width > 120 && $0.bounds.height > 120 }
            .min { $0.bounds.width * $0.bounds.height < $1.bounds.width * $1.bounds.height }
    }

    private static func allScrollViews(in view: NSView) -> [NSScrollView] {
        var result: [NSScrollView] = []
        if let scrollView = view as? NSScrollView {
            result.append(scrollView)
        }
        for subview in view.subviews {
            result.append(contentsOf: allScrollViews(in: subview))
        }
        return result
    }
}

/// How ✓ looks pressed; the closing image draws it the same way.
enum DoneButtonLook {
    static let pressedScale: CGFloat = 1.14
    static let litOpacity: CGFloat = 0.9
    static let symbolSize: CGFloat = 16
}

/// Round Liquid Glass checkmark, like the confirm button on macOS 26
/// system sheets. Return, Esc and the close shortcuts confirm through it too.
private struct DoneGlassButton: View {
    let size: CGFloat
    let action: () -> Void
    /// Held pressed from the click until settings has folded away.
    @State private var isConfirming = false

    /// Long enough for the press to show (the press spring is most of the
    /// way there by then); closing at once on release swapped the page for
    /// its snapshot before the press was ever seen.
    private static let confirmDelay: TimeInterval = 0.14

    var body: some View {
        Button(action: confirm) {
            Image(systemName: "checkmark")
                .font(.system(size: DoneButtonLook.symbolSize, weight: .bold))
                .frame(width: size, height: size)
                .contentShape(Circle())
        }
        .buttonStyle(DoneButtonStyle(isConfirming: isConfirming))
        .keyboardShortcut(.defaultAction)
        // Prepare the closing animation while the pointer is on its way to click.
        .onHover { inside in
            if inside {
                SettingsWindowController.shared.prefetchClosingSnapshot()
            }
        }
        // Where the closing image draws ✓ lit.
        .background {
            GeometryReader { proxy in
                let frame = proxy.frame(in: .global)
                Color.clear
                    .onAppear { SettingsWindowController.shared.doneButtonFrame = frame }
                    .onChange(of: frame) { SettingsWindowController.shared.doneButtonFrame = $0 }
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: SettingsWindowController.confirmCloseNotification)) { _ in
            confirm()
        }
        // Stays lit while folding away; un-lights out of sight.
        .onReceive(NotificationCenter.default.publisher(for: SettingsWindowController.didCloseNotification)) { _ in
            isConfirming = false
        }
        .help("完成")
        .accessibilityLabel("完成")
    }

    private func confirm() {
        SettingsWindowController.shared.doneWillConfirm()
        guard !isConfirming else { return }
        isConfirming = true
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.confirmDelay) {
            action()
        }
    }
}

/// Pressed, the whole glass circle swells clearly and fills with the
/// accent colour, the ✓ turning white, like a system confirm button.
private struct DoneButtonStyle: ButtonStyle {
    let isConfirming: Bool

    func makeBody(configuration: Configuration) -> some View {
        DoneButtonBody(label: configuration.label, isPressed: configuration.isPressed || isConfirming)
    }
}

private struct DoneButtonBody<Label: View>: View {
    @Environment(\.accessibilityLook) private var look
    let label: Label
    let isPressed: Bool
    /// Follows the press, but stays a moment after a quick click, like the
    /// swell itself (see `PressFeedback.minimumHold`).
    @State private var isLit = false
    @State private var pressedAt: CFTimeInterval = 0

    var body: some View {
        label
            .foregroundStyle(isLit ? Color.white : Color.primary)
            // The colour fades in on the glass, rather than re-tinting the
            // glass itself, which swapped it in one step.
            .background {
                Circle()
                    .fill(Color.accentColor)
                    .opacity(isLit ? DoneButtonLook.litOpacity : 0)
            }
            .clipShelfGlass(in: Circle())
            .pressFeedback(isPressed, scale: DoneButtonLook.pressedScale)
            .onChange(of: isPressed) { pressed in
                if pressed {
                    pressedAt = CACurrentMediaTime()
                    withAnimation(.easeOut(duration: look.reduceMotion ? 0 : 0.13)) { isLit = true }
                } else {
                    let wait = max(0, PressFeedback.minimumHold - (CACurrentMediaTime() - pressedAt))
                    DispatchQueue.main.asyncAfter(deadline: .now() + wait) {
                        guard !isPressed else { return }
                        withAnimation(.easeInOut(duration: 0.25)) { isLit = false }
                    }
                }
            }
    }
}

/// While set, settings rows drop their edge blur. Only for the one-off CPU
/// render made before settings is first shown (see `SettingsWindowController`);
/// that blur makes such a render ~25× slower.
final class SettingsSnapshotMode: ObservableObject {
    static let shared = SettingsSnapshotMode()
    @Published var isRendering = false
}

final class GlassPreviewModel: ObservableObject {
    @Published var level: Double
    init(level: Double) { self.level = level }
}

/// A small window-like scene: a landscape behind the base glass tint, with a
/// record card of real glass on it, at the slider's current level.
private struct GlassPreviewSample: View {
    @ObservedObject var model: GlassPreviewModel
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let transparency = GlassTransparency(level: model.level)
        let isDark = colorScheme == .dark
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
        ZStack {
            Landscape()
            // The window's base tint over the desktop, as the real base.
            Rectangle().fill(isDark
                ? Color(white: 0.06).opacity(transparency.darkWindowSmoke)
                : Color(white: 0.6).opacity(transparency.lightWindowTint + 0.2))
            HStack(spacing: 10) {
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .fill(LinearGradient(colors: [Color(red: 0.98, green: 0.72, blue: 0.4), Color(red: 0.9, green: 0.45, blue: 0.5)], startPoint: .topLeading, endPoint: .bottomTrailing))
                    .frame(width: 34, height: 34)
                VStack(alignment: .leading, spacing: 3) {
                    Text("截屏 2026-10-28 21.43.59.png")
                        .font(.system(size: 12.5, weight: .semibold))
                        .lineLimit(1)
                    Text("截图  刚刚")
                        .font(.system(size: 10.5, weight: .medium))
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 12)
            .frame(height: 52)
            .cardGlass()
            .environment(\.glassTransparency, transparency)
            .padding(.horizontal, 18)
        }
        .frame(height: 96)
        // Eases between drag steps instead of jumping.
        .animation(.easeOut(duration: 0.14), value: model.level)
        .clipShape(shape)
        .overlay(shape.strokeBorder(Color.primary.opacity(0.08), lineWidth: 0.5))
        .accessibilityHidden(true)
    }
}

/// A simple drawn landscape: sky, sun and two rows of hills.
private struct Landscape: View {
    var body: some View {
        GeometryReader { proxy in
            let w = proxy.size.width, h = proxy.size.height
            ZStack {
                LinearGradient(colors: [Color(red: 0.36, green: 0.6, blue: 0.92), Color(red: 0.98, green: 0.78, blue: 0.62)], startPoint: .top, endPoint: .bottom)
                Circle().fill(Color(red: 1, green: 0.88, blue: 0.55)).frame(width: 30, height: 30).position(x: w * 0.78, y: h * 0.32)
                Path { p in
                    p.move(to: CGPoint(x: 0, y: h * 0.7))
                    p.addCurve(to: CGPoint(x: w, y: h * 0.55), control1: CGPoint(x: w * 0.3, y: h * 0.35), control2: CGPoint(x: w * 0.6, y: h * 0.8))
                    p.addLine(to: CGPoint(x: w, y: h)); p.addLine(to: CGPoint(x: 0, y: h)); p.closeSubpath()
                }
                .fill(Color(red: 0.32, green: 0.55, blue: 0.4))
                Path { p in
                    p.move(to: CGPoint(x: 0, y: h * 0.88))
                    p.addCurve(to: CGPoint(x: w, y: h * 0.8), control1: CGPoint(x: w * 0.35, y: h * 0.7), control2: CGPoint(x: w * 0.7, y: h * 1.0))
                    p.addLine(to: CGPoint(x: w, y: h)); p.addLine(to: CGPoint(x: 0, y: h)); p.closeSubpath()
                }
                .fill(Color(red: 0.2, green: 0.4, blue: 0.3))
            }
        }
    }
}

enum SettingsPanelSpace {
    static let name = "ClipShelfSettingsPanel"
}

/// A progressive frosted band: fully blurred at the top of the panel, fading
/// to nothing at its bottom edge.
private struct EdgeBlurBand: View {
    @Environment(\.glassStandIn) private var standIn
    @Environment(\.colorScheme) private var colorScheme
    /// Which panel edge the band is fully frosted at.
    let edge: VerticalEdge

    var body: some View {
        // Drawn upright for both edges and only the fade reversed: a flipped
        // band also flipped how its glass bends the light.
        Rectangle()
            // The page's CPU render (the closing animation's image) can't
            // show what is behind anyway, and blurring there made each
            // render take ~0.3 s on the main thread: a flat frost instead.
            .fill(standIn ? AnyShapeStyle(Color(white: colorScheme == .dark ? 0.16 : 0.9).opacity(0.85)) : AnyShapeStyle(.ultraThinMaterial))
            .mask(
                LinearGradient(
                    stops: [
                        .init(color: .black, location: 0),
                        .init(color: .black, location: 0.45),
                        .init(color: .black.opacity(0.55), location: 0.72),
                        .init(color: .black.opacity(0.18), location: 0.9),
                        .init(color: .black.opacity(0), location: 1)
                    ],
                    startPoint: edge == .top ? .top : .bottom,
                    endPoint: edge == .top ? .bottom : .top
                )
            )
            .allowsHitTesting(false)
    }
}
