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
                .padding(.bottom, 28)
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
                        .frame(height: 40)
                        .clipShelfGlass(in: Capsule())
                    Spacer()
                    DoneGlassButton {
                        commitHistoryLimit()
                        onClose()
                    }
                }
                .padding(.horizontal, 22)
                .padding(.top, 10)
                // Frosts what scrolls under the header: exactly from the top
                // of the panel to the bottom edge of the 设置 capsule.
                .background { HeaderBlurBand() }
            }
            .background(OverlayScrollerConfigurator())
            // The same progressive frost along the bottom edge (one band for
            // the whole panel; a blur on every row re-ran on every scroll frame).
            .overlay(alignment: .bottom) {
                HeaderBlurBand()
                    .rotationEffect(.degrees(180))
                    .frame(height: 26)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .overlay { LiquidMenuOverlay() }
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
                LiquidSegmentedControl(
                    options: ScrollEffectStyle.allCases,
                    selection: Binding(
                        get: { scrollEffect },
                        set: { value in
                            scrollEffect = value
                            ScrollEffectPreferences.value = value
                        }
                    ),
                    title: \.title,
                    segmentWidth: 76,
                    height: SettingsControlMetrics.height
                )
                .accessibilityLabel("滚动效果")
            }

            settingsCaption("渐进模糊：记录滑到顶部工具栏下或滑出底部时，随滑入的程度逐渐模糊。")

            HStack {
                Text("切换动画")
                Spacer()
                LiquidSegmentedControl(
                    options: ListEntranceStyle.allCases,
                    selection: Binding(
                        get: { listEntrance },
                        set: { value in
                            listEntrance = value
                            ListEntrancePreferences.value = value
                        }
                    ),
                    title: \.title,
                    segmentWidth: 84,
                    height: SettingsControlMetrics.height
                )
                .accessibilityLabel("切换动画")
            }

            settingsCaption("切换记录类型时，新记录出现的方式。")
        }
    }

    private var glassSection: some View {
        settingsSection("玻璃效果") {
            GlassLevelSlider(
                value: Binding(
                    get: { glassTransparency.level },
                    set: { glassTransparency = GlassTransparency(level: $0) }
                ),
                onPreview: { SettingsWindowController.shared.previewGlass(GlassTransparency(level: $0)) },
                onCommit: { GlassPreferences.value = GlassTransparency(level: $0) }
            )

            settingsCaption("往左更清透，往右玻璃着色更重、更易读。拖动时先预览设置面板，松手后主界面再跟上。")

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

            settingsCaption("点击快捷键按钮后按新的组合键，按 Esc 取消录入。设置窗口也可以用 ⌘, 或 Esc 打开 / 关闭。")
        }
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
            Text(title)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Color.primary.opacity(resolvedColorScheme == .dark ? 0.78 : 0.66))

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

/// Round, tinted Liquid Glass checkmark, like the confirm button on macOS 26
/// system sheets. Return also triggers it.
private struct DoneGlassButton: View {
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: "checkmark")
                .font(.system(size: 15, weight: .bold))
                .frame(width: 22, height: 22)
        }
        .keyboardShortcut(.defaultAction)
        .controlSize(.large)
        .modifier(CircleButtonShape())
        // Prepare the closing animation while the pointer is on its way to click.
        .onHover { inside in
            if inside {
                SettingsWindowController.shared.prefetchClosingSnapshot()
            }
        }
        .clipShelfGlassButtons()
        .help("完成")
        .accessibilityLabel("完成")
    }
}

private struct CircleButtonShape: ViewModifier {
    func body(content: Content) -> some View {
        if #available(macOS 14.0, *) {
            content.buttonBorderShape(.circle)
        } else {
            content.buttonBorderShape(.roundedRectangle)
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

enum SettingsPanelSpace {
    static let name = "ClipShelfSettingsPanel"
}

/// A progressive frosted band: fully blurred at the top of the panel, fading
/// to nothing at its bottom edge.
private struct HeaderBlurBand: View {
    var body: some View {
        Rectangle()
            .fill(.ultraThinMaterial)
            .mask(
                LinearGradient(
                    stops: [
                        .init(color: .black, location: 0),
                        .init(color: .black, location: 0.45),
                        .init(color: .black.opacity(0), location: 1)
                    ],
                    startPoint: .top,
                    endPoint: .bottom
                )
            )
            .allowsHitTesting(false)
    }
}
