import AppKit
import SwiftUI

struct MainView: View {
    private struct SearchSignature: Equatable {
        let ids: [ClipItem.ID]
        let kind: ClipKindFilter
        let query: String
    }

    @ObservedObject var store: ClipStore
    @ObservedObject var watcher: ScreenshotFolderWatcher
    @State private var searchText = ""
    @State private var kindFilter: ClipKindFilter = HistoryFilterPreferences.value
    @State private var showingSettings = false
    @State private var selectedIDs = Set<ClipItem.ID>()
    @State private var focusedID: ClipItem.ID?
    @State private var anchorID: ClipItem.ID?
    @State private var isDragSelecting = false
    @State private var dragAnchorID: ClipItem.ID?
    @State private var dragSnapshotItems: [ClipItem]?
    @State private var historyScrollView: NSScrollView?
    @State private var selectionColor = SelectionColorPreferences.color
    @State private var selectionColorPreset = SelectionColorPreferences.selectedPreset
    @State private var switchToClickedRecord = SelectionClickBehaviorPreferences.switchToClickedRecord
    @State private var multiSelectionClickSelectedBehavior = SelectionClickBehaviorPreferences.multiSelectionClickSelectedBehavior
    @State private var multiSelectionClickUnselectedBehavior = SelectionClickBehaviorPreferences.multiSelectionClickUnselectedBehavior
    @State private var postDragClickRecoveryDuration = DragSelectionPreferences.clickRecoveryDuration
    @State private var commandKeyMonitor: Any?
    @State private var appIconChoice = AppIconPreferences.selected
    @State private var clearSelectionHotKey = ClearSelectionHotKeyDefaults.load()
    @State private var pinHotKey = PinHotKeyDefaults.load()
    @State private var suppressRowTapUntil = Date.distantPast
    @StateObject private var updateChecker = AppUpdateChecker.shared
    @State private var hostWindow: NSWindow?
    @State private var appearanceMode = AppearancePreferences.mode
    @State private var systemColorScheme = AppearancePreferences.systemColorScheme
    @State private var showingClearHistoryConfirmation = false
    @State private var lastRevealedNewest: Date?
    @State private var filteredItemsCache = [ClipItem]()
    @State private var filteredItemsSignature: SearchSignature?
    @State private var searchIndexCache = ClipSearchIndexCache()
    @State private var imageThumbnailCache = ImageThumbnailCache()
    @FocusState private var isSearchFocused: Bool
    @State private var toolbarHeight: CGFloat = 160
    @State private var titlebarHeight: CGFloat = 28
    private let historyRowHeight: CGFloat = 74
    private let historyListHorizontalInset: CGFloat = 18

    /// The glass toolbar floats over the top of the list, so rows start below it
    /// and every viewport calculation treats that strip as covered.
    private var historyListVerticalInset: CGFloat { toolbarHeight + 6 }

    private var searchSignature: SearchSignature {
        SearchSignature(ids: store.items.map(\.id), kind: kindFilter, query: searchText)
    }

    private var liveFilteredItems: [ClipItem] {
        filteredItemsCache
    }

    private var filteredItems: [ClipItem] {
        dragSnapshotItems ?? liveFilteredItems
    }

    private var resolvedColorScheme: ColorScheme {
        appearanceMode == .system ? systemColorScheme : (appearanceMode.colorScheme ?? .light)
    }

    var body: some View {
        ZStack(alignment: .top) {
            if filteredItems.isEmpty {
                emptyView
                    .padding(.top, toolbarHeight)
            } else {
                historyList
            }

            toolbar
                .background(
                    GeometryReader { proxy in
                        Color.clear.preference(key: ToolbarHeightKey.self, value: proxy.size.height)
                    }
                )
        }
        .ignoresSafeArea(.container, edges: .top)
        .onPreferenceChange(ToolbarHeightKey.self) { height in
            let rounded = height.rounded()
            if abs(rounded - toolbarHeight) > 0.5 {
                toolbarHeight = rounded
            }
        }
        .frame(minWidth: 680, minHeight: 552)
        .preferredColorScheme(resolvedColorScheme)
        .overlay {
            ZStack {
                if showingSettings {
                    settingsOverlay
                        .transition(
                            .asymmetric(
                                insertion: .opacity.combined(with: .scale(scale: 0.97, anchor: .center)),
                                removal: .opacity.combined(with: .scale(scale: 0.99, anchor: .center))
                            )
                        )
                        .zIndex(10)
                }
            }
        }
        .animation(.spring(response: 0.26, dampingFraction: 0.88), value: showingSettings)
        .background(WindowReader { window in
            hostWindow = window
            if let window {
                let height = window.frame.height - window.contentLayoutRect.height
                if height > 0, abs(height - titlebarHeight) > 0.5 {
                    titlebarHeight = height
                }
            }
        })
        .confirmationDialog(
            ClearHistoryConfirmation.title(itemCount: store.items.count),
            isPresented: $showingClearHistoryConfirmation,
            titleVisibility: .visible
        ) {
            Button("清空", role: .destructive) {
                store.clearHistory()
                clearSelection()
            }
            Button("取消", role: .cancel) { }
        } message: {
            Text(ClearHistoryConfirmation.message(itemCount: store.items.count))
        }
        .onAppear {
            refreshFilteredItemsIfNeeded()
            prewarmSearchIndexes(for: store.items)
            imageThumbnailCache.setMaximumEntryCount(store.items.count + 8)
            lastRevealedNewest = HistoryScrollTarget.newestDate(in: store.items)
            installCommandKeyMonitorIfNeeded()
            updateChecker.checkIfNeeded()
        }
        .onDisappear {
            endDragSelection()
            removeCommandKeyMonitor()
        }
        .onReceive(NotificationCenter.default.publisher(for: AppearancePreferences.changedNotification)) { notification in
            appearanceMode = notification.object as? AppearanceMode ?? AppearancePreferences.mode
            systemColorScheme = AppearancePreferences.systemColorScheme
        }
        .onReceive(NotificationCenter.default.publisher(for: AppearancePreferences.systemChangedNotification)) { _ in
            systemColorScheme = AppearancePreferences.systemColorScheme
        }
        .onReceive(NotificationCenter.default.publisher(for: .clipShelfOpenSettings)) { _ in
            openSettings()
        }
    }

    private var settingsOverlay: some View {
        GeometryReader { proxy in
            let panelWidth = min(max(proxy.size.width - 96, 560), 760)
            let panelHeight = max(proxy.size.height - 96, 360)

            ZStack {
                    AppTheme.overlayBackground
                    .ignoresSafeArea()
                    .contentShape(Rectangle())
                    .onTapGesture {
                        closeSettings()
                    }

                SettingsView(store: store, watcher: watcher) {
                    closeSettings()
                }
                .frame(width: panelWidth, height: panelHeight)
                .clipShelfGlassPanel(cornerRadius: 26)
                .contentShape(Rectangle())
                .onTapGesture { }
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
        }
    }

    private var historyList: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(Array(filteredItems.enumerated()), id: \.element.id) { index, item in
                        let isSelected = selectedIDs.contains(item.id)
                        let isNextSelected = index + 1 < filteredItems.count && selectedIDs.contains(filteredItems[index + 1].id)
                        ClipRow(
                            item: item,
                            store: store,
                            isFocused: focusedID == item.id,
                            isSelected: isSelected,
                            selectionColor: selectionColor,
                            selectionColorIsPreset: selectionColorPreset != nil,
                            imageThumbnailCache: imageThumbnailCache,
                            showsSeparator: index < filteredItems.count - 1 && !isSelected && !isNextSelected,
                            handleClick: { event in handleRowClick(item, event: event) },
                            handleCopy: { store.copy(actionItems(for: item)) }
                        )
                        .id(item.id)
                    }
                }
                .padding(.horizontal, historyListHorizontalInset)
                .padding(.top, historyListVerticalInset)
                .padding(.bottom, 18)
            }
            .coordinateSpace(name: "historyList")
            .background(
                ScrollViewResolver { scrollView in
                    historyScrollView = scrollView
                }
            )
            .background(KeyboardCaptureView { event in
                handleKey(event, scrollProxy: proxy)
            })
            .background(
                DragSelectionCaptureView(
                    isEnabled: !showingSettings,
                    clickRecoveryDuration: postDragClickRecoveryDuration,
                    itemIDs: filteredItems.map(\.id),
                    rowHeight: historyRowHeight,
                    contentTopInset: historyListVerticalInset,
                    viewportTopObstruction: toolbarHeight,
                    contentHorizontalInset: historyListHorizontalInset,
                    onPointerDown: { location in
                        handleHistoryPointerDown(at: location)
                    },
                    onSmallDragClick: { location, event in
                        handleHistorySmallDragClick(at: location, event: event)
                    },
                    onDragBegan: {
                        beginDragSelectionSnapshot()
                    },
                    onSelectionChanged: { update in
                        applyDragSelection(update)
                    },
                    onEnd: {
                        endDragSelection()
                    },
                    onCancel: {
                        endDragSelection()
                    }
                )
            )
            .onChange(of: HistoryScrollTarget.newestAnchorID(in: store.items)) { newID in
                guard let newID, !isDragSelecting else { return }
                guard HistoryScrollTarget.anchorToReveal(
                    in: store.items,
                    lastRevealedNewest: lastRevealedNewest
                ) == newID else { return }
                withAnimation(.easeOut(duration: 0.18)) {
                    proxy.scrollTo(newID, anchor: .top)
                }
                lastRevealedNewest = HistoryScrollTarget.newestDate(in: store.items)
            }
            .onChange(of: searchSignature) { _ in
                refreshFilteredItemsIfNeeded()
            }
            .onChange(of: store.items.map(\.id)) { _ in
                prewarmSearchIndexes(for: store.items)
                imageThumbnailCache.setMaximumEntryCount(store.items.count + 8)
            }
            .onChange(of: searchText) { _ in
                refreshFilteredItemsIfNeeded()
                clearSelection()
            }
            .onChange(of: kindFilter) { _ in
                refreshFilteredItemsIfNeeded()
                clearSelection()
                HistoryFilterPreferences.value = kindFilter
                DispatchQueue.main.async {
                    scrollHistoryToTop()
                }
            }
            .onReceive(NotificationCenter.default.publisher(for: NSApplication.didResignActiveNotification)) { _ in
                clearSelection()
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
                postDragClickRecoveryDuration = notification.object as? TimeInterval ?? DragSelectionPreferences.clickRecoveryDuration
                if postDragClickRecoveryDuration <= 0 {
                    suppressRowTapUntil = .distantPast
                }
            }
            .onReceive(NotificationCenter.default.publisher(for: AppIconPreferences.changedNotification)) { notification in
                appIconChoice = notification.object as? AppIconChoice ?? AppIconPreferences.selected
            }
            .onReceive(NotificationCenter.default.publisher(for: ClearSelectionHotKeyDefaults.changedNotification)) { notification in
                clearSelectionHotKey = notification.object as? HotKeyConfiguration ?? ClearSelectionHotKeyDefaults.load()
            }
            .onReceive(NotificationCenter.default.publisher(for: PinHotKeyDefaults.changedNotification)) { notification in
                pinHotKey = notification.object as? HotKeyConfiguration ?? PinHotKeyDefaults.load()
            }
        }
    }

    private var toolbar: some View {
        VStack(spacing: 10) {
            titleRow
            searchField

            HStack(spacing: 12) {
                HistoryTypeFilterBar(selection: $kindFilter)

                Spacer(minLength: 8)
                primaryToolbar
            }
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 12)
        // Gaps between controls must not click through to rows hidden underneath.
        .contentShape(Rectangle())
        .background(alignment: .top) {
            TitlebarScrollEdge(height: max(titlebarHeight, 28) + 6)
        }
    }

    /// Lives in the (transparent) titlebar strip, next to the traffic lights.
    private var titleRow: some View {
        HStack(spacing: 8) {
            Color.clear.frame(width: 62)

            Group {
                if let image = appIconChoice.previewImage {
                    Image(nsImage: image)
                        .resizable()
                        .scaledToFit()
                } else {
                    Image(systemName: "doc.on.clipboard")
                        .font(.system(size: 28, weight: .semibold))
                        .foregroundStyle(.secondary)
                }
            }
            .frame(width: 36, height: 36)

            Text("ClipShelf")
                .font(.system(size: 17, weight: .semibold))

            Text(AppVersionInfo.statusVersion(bundleVersion: AppVersionInfo.bundleVersion))
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(.secondary)
                .padding(.top, 3)

            Spacer()

            if let update = updateChecker.availableUpdate {
                Button {
                    updateChecker.openUpdatePage()
                } label: {
                    Label("更新", systemImage: "arrow.down.circle")
                        .labelStyle(.titleAndIcon)
                }
                .controlSize(.small)
                .clipShelfLiquidGlassButtonStyle()
                .foregroundStyle(Color.accentColor)
                .floatingTooltip("发现新版本 \(update.versionText)，点击打开下载页")
            }
        }
        .frame(height: max(titlebarHeight, 44))
        .background(WindowDragHandle())
    }

    private var searchField: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(.secondary)
            TextField("搜索文字、文件名、截图名", text: $searchText)
                .textFieldStyle(.plain)
                .font(.system(size: 14))
                .focused($isSearchFocused)
            if let selectionCountText = SelectionCountLabel.text(for: selectedIDs.count) {
                Text(selectionCountText)
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .fixedSize()
            }
            if !searchText.isEmpty {
                Button {
                    searchText = ""
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .foregroundStyle(.tertiary)
                }
                .buttonStyle(.plain)
                .floatingTooltip("清除搜索")
            }
        }
        .padding(.horizontal, 14)
        .frame(height: 38)
        .clipShelfLiquidGlassSurface(cornerRadius: 19)
    }

    @ViewBuilder
    private var primaryToolbar: some View {
        if #available(macOS 26.0, *) {
            GlassEffectContainer(spacing: 8) {
                primaryToolbarButtons
            }
        } else {
            primaryToolbarButtons
        }
    }

    /// Two glass capsules, like system toolbar groups: places, then actions.
    private var primaryToolbarButtons: some View {
        HStack(spacing: 10) {
            HStack(spacing: 0) {
                toolbarButton("folder", tooltip: "选择截图文件夹") {
                    debugFolderPickerLog("choose folder button clicked")
                    watcher.chooseFolder(attachedTo: hostWindow)
                }
                toolbarButton("gearshape", tooltip: "设置") {
                    openSettings()
                }
            }
            .clipShelfGlassGroup()

            HStack(spacing: 0) {
                toolbarButton("doc.on.doc", tooltip: copyActionHelp, isDimmed: actionItems.isEmpty) {
                    copyActionItems()
                }
                toolbarButton("pin", tooltip: "置顶选中的记录", isDimmed: actionItems.isEmpty) {
                    togglePinnedForActionItems()
                }
                toolbarButton("trash", tooltip: selectedIDs.isEmpty ? "清空记录" : "删除选中的记录") {
                    deleteSelectedOrClear()
                }
                toolbarButton("arrow.uturn.backward", tooltip: "撤销最近一批删除", isDimmed: !store.canUndoDeletion) {
                    undoLastDeletion()
                }
                .disabled(!store.canUndoDeletion)
            }
            .clipShelfGlassGroup()
        }
    }

    private func toolbarButton(
        _ systemImage: String,
        tooltip: String,
        isDimmed: Bool = false,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Image(systemName: systemImage)
        }
        .buttonStyle(GlassToolbarIconButtonStyle(isDimmed: isDimmed))
        .floatingTooltip(tooltip)
    }

    private func openSettings() {
        withAnimation(.spring(response: 0.26, dampingFraction: 0.88)) {
            showingSettings = true
        }
    }

    private func closeSettings() {
        withAnimation(.easeOut(duration: 0.16)) {
            showingSettings = false
        }
    }

    private func refreshFilteredItemsIfNeeded() {
        let signature = searchSignature
        guard filteredItemsSignature != signature else { return }
        filteredItemsCache = ClipHistoryFilter.items(
            store.items,
            kind: kindFilter,
            query: searchText,
            indexCache: searchIndexCache
        )
        filteredItemsSignature = signature
    }

    private func prewarmSearchIndexes(for items: [ClipItem]) {
        let cache = searchIndexCache
        DispatchQueue.global(qos: .utility).async {
            cache.prewarm(items)
        }
    }

    private func deleteSelectedOrClear() {
        if selectedIDs.isEmpty {
            guard ClearHistoryConfirmation.shouldConfirm(itemCount: store.items.count) else {
                clearSelection()
                return
            }
            showingClearHistoryConfirmation = true
        } else {
            store.remove(ids: selectedIDs)
            clearSelection()
        }
    }

    private func clearSelection() {
        selectedIDs.removeAll()
        focusedID = nil
        anchorID = nil
        dragAnchorID = nil
        isDragSelecting = false
        dragSnapshotItems = nil
    }

    private func undoLastDeletion() {
        let restored = store.undoLastDeletion()
        guard !restored.isEmpty else { return }
        clearSelection()
    }

    private func handleKey(_ event: NSEvent, scrollProxy: ScrollViewProxy? = nil) {
        if event.modifierFlags.contains(.command) {
            handleCommandKey(event)
            return
        }

        switch event.keyCode {
        case 126:
            moveFocus(delta: -1)
        case 125:
            moveFocus(delta: 1)
        case 49:
            let items = previewItems
            if !items.isEmpty {
                PreviewController.shared.togglePreview(items, onNavigate: navigatePreview)
            }
        case 51, 117:
            deleteSelectedOrClear()
        default:
            break
        }
    }

    private func installCommandKeyMonitorIfNeeded() {
        guard commandKeyMonitor == nil else { return }
        commandKeyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            guard shouldHandleMainWindowKeyEvent(event) else {
                return event
            }

            if clearSelectionHotKey.matches(event) {
                clearSelection()
                return nil
            }

            if pinHotKey.matches(event) {
                togglePinnedForActionItems()
                return nil
            }

            if event.keyCode == 53 {
                if !searchText.isEmpty {
                    clearSearchState()
                    return nil
                }
            }

            if (event.keyCode == 125 || event.keyCode == 126),
               !event.modifierFlags.contains(.command),
               !event.modifierFlags.contains(.option),
               !event.modifierFlags.contains(.control) {
                handleKey(event)
                return nil
            }

            if event.modifierFlags.contains(.command),
               event.charactersIgnoringModifiers?.lowercased() == "z",
               isSearchFocused {
                return event
            }

            if event.modifierFlags.contains(.command),
               ["a", "c", "z"].contains(event.charactersIgnoringModifiers?.lowercased()) {
                handleCommandKey(event)
                return nil
            }

            return event
        }
    }

    private func shouldHandleMainWindowKeyEvent(_ event: NSEvent) -> Bool {
        guard !showingSettings,
              let window = event.window,
              window === NSApp.keyWindow,
              window.title.hasPrefix("ClipShelf") else {
            return false
        }

        return true
    }

    private func removeCommandKeyMonitor() {
        if let commandKeyMonitor {
            NSEvent.removeMonitor(commandKeyMonitor)
            self.commandKeyMonitor = nil
        }
    }

    private func handleCommandKey(_ event: NSEvent) {
        switch event.charactersIgnoringModifiers?.lowercased() {
        case "a":
            selectAllVisibleItems()
        case "c":
            store.copy(actionItems)
        case "z":
            guard !isSearchFocused else { return }
            undoLastDeletion()
        default:
            break
        }
    }

    private func clearSearchState() {
        searchText = ""
        clearSelection()
        NSApp.keyWindow?.makeFirstResponder(nil)
    }

    private var focusedItem: ClipItem? {
        guard let focusedID else { return nil }
        return filteredItems.first { $0.id == focusedID }
    }

    private var actionItems: [ClipItem] {
        let selected = filteredItems.filter { selectedIDs.contains($0.id) }
        if !selected.isEmpty {
            return selected
        }

        if let focusedItem {
            return [focusedItem]
        }

        return []
    }

    private func actionItems(for rowItem: ClipItem) -> [ClipItem] {
        if selectedIDs.contains(rowItem.id) {
            let selected = filteredItems.filter { selectedIDs.contains($0.id) }
            if !selected.isEmpty {
                return selected
            }
        }

        return [rowItem]
    }

    private var copyActionHelp: String {
        selectedIDs.count > 1 ? "复制选中的 \(selectedIDs.count) 条记录" : "复制当前记录"
    }

    private func copyActionItems() {
        let items = actionItems
        guard !items.isEmpty else { return }
        store.copy(items)
    }

    private func selectAllVisibleItems() {
        selectedIDs = Set(filteredItems.map(\.id))
        focusedID = filteredItems.first?.id
        anchorID = focusedID
    }

    private func togglePinnedForActionItems() {
        let items = actionItems
        let ids = Set(items.map(\.id))
        guard !ids.isEmpty else { return }
        store.togglePinned(ids: ids)
        selectedIDs = ids
        focusedID = items.first?.id ?? focusedID
        anchorID = focusedID
    }

    private func moveFocus(delta: Int) {
        guard !filteredItems.isEmpty else {
            focusedID = nil
            selectedIDs.removeAll()
            anchorID = nil
            return
        }

        let currentIndex: Int
        if let focusedID, let index = filteredItems.firstIndex(where: { $0.id == focusedID }) {
            currentIndex = index
        } else {
            currentIndex = initialKeyboardIndex(for: delta)
        }
        let nextIndex = min(max(currentIndex + delta, 0), filteredItems.count - 1)
        guard nextIndex != currentIndex else { return }

        let nextID = filteredItems[nextIndex].id
        focusedID = nextID
        selectedIDs = [nextID]
        anchorID = nextID

        revealKeyboardSelectionIfNeeded(nextID, direction: delta)
    }

    private func initialKeyboardIndex(for delta: Int) -> Int {
        let visibleIndices = visibleItemIndices()
        if delta > 0 {
            return (visibleIndices.first ?? -1) - 1
        }

        return (visibleIndices.last ?? filteredItems.count) + 1
    }

    private func revealKeyboardSelectionIfNeeded(_ id: ClipItem.ID, direction: Int) {
        guard let scrollView = historyScrollView,
              let index = filteredItems.firstIndex(where: { $0.id == id }) else { return }

        if let rowFrame = rowFrameInViewport(at: index, in: scrollView),
           let delta = scrollDeltaToReveal(rowFrame, in: scrollView) {
            scrollHistoryViewByGlobalDelta(delta, scrollView: scrollView)
        }
    }

    private func scrollDeltaToReveal(_ rowFrame: CGRect, in scrollView: NSScrollView) -> CGFloat? {
        let visibleHeight = scrollView.contentView.bounds.height
        guard visibleHeight > 0 else { return nil }

        let topPadding: CGFloat = 6
        let bottomPadding: CGFloat = 18
        let visibleTop = toolbarHeight + topPadding
        let visibleBottom = visibleHeight - bottomPadding

        if rowFrame.minY < visibleTop {
            return rowFrame.minY - visibleTop
        }

        if rowFrame.maxY > visibleBottom {
            return rowFrame.maxY - visibleBottom
        }

        return nil
    }

    private func rowFrameInViewport(at index: Int, in scrollView: NSScrollView) -> CGRect? {
        guard filteredItems.indices.contains(index),
              let visibleRange = visibleContentRange(in: scrollView) else { return nil }

        let rowMinY = historyListVerticalInset + CGFloat(index) * historyRowHeight
        return CGRect(
            x: 0,
            y: rowMinY - visibleRange.lowerBound,
            width: scrollView.contentView.bounds.width,
            height: historyRowHeight
        )
    }

    private func visibleContentRange(in scrollView: NSScrollView) -> ClosedRange<CGFloat>? {
        guard let documentView = scrollView.documentView else { return nil }

        let visible = scrollView.contentView.bounds
        guard visible.height > 0 else { return nil }

        if documentView.isFlipped {
            return visible.minY...visible.maxY
        }

        let documentHeight = documentView.bounds.height
        return max(0, documentHeight - visible.maxY)...max(0, documentHeight - visible.minY)
    }

    private func scrollHistoryViewByGlobalDelta(_ delta: CGFloat, scrollView: NSScrollView) {
        guard let documentView = scrollView.documentView else { return }
        let clipView = scrollView.contentView
        let visible = clipView.bounds
        let maxY = max(0, documentView.bounds.height - visible.height)
        let sign: CGFloat = documentView.isFlipped ? 1 : -1
        let proposedY = min(max(visible.origin.y + delta * sign, 0), maxY)
        guard proposedY != visible.origin.y else { return }

        clipView.scroll(to: CGPoint(x: visible.origin.x, y: proposedY))
        scrollView.reflectScrolledClipView(clipView)
    }

    private func scrollHistoryToTop() {
        guard let scrollView = historyScrollView,
              let documentView = scrollView.documentView else { return }

        let clipView = scrollView.contentView
        let maxY = max(0, documentView.bounds.height - clipView.bounds.height)
        let topY = documentView.isFlipped ? 0 : maxY
        clipView.scroll(to: CGPoint(x: clipView.bounds.origin.x, y: topY))
        scrollView.reflectScrolledClipView(clipView)
    }

    private func visibleItemIndices() -> [Int] {
        guard let scrollView = historyScrollView,
              !filteredItems.isEmpty,
              let range = visibleContentRange(in: scrollView) else { return [] }

        let first = max(0, Int(floor((range.lowerBound + toolbarHeight - historyListVerticalInset) / historyRowHeight)))
        let last = min(filteredItems.count - 1, Int(floor((range.upperBound - historyListVerticalInset) / historyRowHeight)))
        guard first <= last else { return [] }

        return Array(first...last)
    }

    private func toggleSelectionForKeyboard(_ id: ClipItem.ID) {
        if selectedIDs.contains(id) {
            selectedIDs.remove(id)
        } else {
            selectedIDs.insert(id)
        }
        anchorID = id
    }

    private func handleRowClick(_ item: ClipItem, event: NSEvent?) {
        guard Date() >= suppressRowTapUntil else { return }

        let modifiers = event?.modifierFlags ?? []
        if modifiers.contains(.shift), let anchorID {
            focusedID = item.id
            selectRange(from: anchorID, to: item.id)
        } else if modifiers.contains(.command) {
            focusedID = item.id
            if selectedIDs.contains(item.id) {
                selectedIDs.remove(item.id)
            } else {
                selectedIDs.insert(item.id)
            }
            anchorID = item.id
        } else if selectedIDs.count > 1 {
            handleMultiSelectionClick(on: item)
        } else if selectedIDs.count == 1 {
            handleSingleSelectionClick(on: item)
        } else {
            collapseSelection(to: item)
        }
    }

    private func handleSingleSelectionClick(on item: ClipItem) {
        if selectedIDs.contains(item.id) || !switchToClickedRecord {
            clearSelection()
        } else {
            collapseSelection(to: item)
        }
    }

    private func handleMultiSelectionClick(on item: ClipItem) {
        if selectedIDs.contains(item.id) {
            switch multiSelectionClickSelectedBehavior {
            case .collapseToClicked:
                collapseSelection(to: item)
            case .clearAll:
                clearSelection()
            case .removeClicked:
                removeClickedItemFromMultiSelection(item)
            }
        } else {
            switch multiSelectionClickUnselectedBehavior {
            case .collapseToClicked:
                collapseSelection(to: item)
            case .clearAll:
                clearSelection()
            }
        }
    }

    private func removeClickedItemFromMultiSelection(_ item: ClipItem) {
        selectedIDs.remove(item.id)
        guard selectedIDs.count > 1 else {
            clearSelection()
            return
        }

        focusedID = selectedIDs.first
        anchorID = focusedID
    }

    private func collapseSelection(to item: ClipItem) {
        focusedID = item.id
        selectedIDs = [item.id]
        anchorID = item.id
    }

    private func handleHistoryPointerDown(at location: CGPoint) {
        guard !selectedIDs.isEmpty || focusedID != nil else { return }
        guard rowID(at: location) == nil else { return }
        clearSelection()
    }

    private func handleHistorySmallDragClick(at location: CGPoint, event: NSEvent?) {
        guard let id = rowID(at: location),
              let item = filteredItems.first(where: { $0.id == id }) else { return }
        if postDragClickRecoveryDuration > 0 {
            suppressRowTapUntil = Date().addingTimeInterval(0.18)
        }
        collapseSelectionFromRecoveredClick(to: item)
    }

    private func collapseSelectionFromRecoveredClick(to item: ClipItem) {
        if selectedIDs.count > 1 {
            handleMultiSelectionClick(on: item)
        } else if selectedIDs.count == 1 {
            handleSingleSelectionClick(on: item)
        } else {
            collapseSelection(to: item)
        }
    }

    private func selectRange(from firstID: ClipItem.ID, to secondID: ClipItem.ID) {
        guard let firstIndex = filteredItems.firstIndex(where: { $0.id == firstID }),
              let secondIndex = filteredItems.firstIndex(where: { $0.id == secondID }) else {
            selectedIDs = [secondID]
            anchorID = secondID
            return
        }

        let bounds = min(firstIndex, secondIndex)...max(firstIndex, secondIndex)
        selectedIDs = Set(bounds.map { filteredItems[$0].id })
    }

    private var previewItems: [ClipItem] {
        let selected = filteredItems.filter { selectedIDs.contains($0.id) }
        if selected.count == 1 {
            return selected
        }

        if selected.count > 1 {
            return []
        }

        if let focusedItem {
            return [focusedItem]
        }

        return []
    }

    private func navigatePreview(direction: Int) -> Bool {
        guard !filteredItems.isEmpty else { return false }

        let currentID = focusedID ?? selectedIDs.first
        guard let currentID,
              let currentIndex = filteredItems.firstIndex(where: { $0.id == currentID }) else {
            return false
        }

        let nextIndex = min(max(currentIndex + direction, 0), filteredItems.count - 1)
        guard nextIndex != currentIndex else { return false }

        let item = filteredItems[nextIndex]
        collapseSelection(to: item)
        revealKeyboardSelectionIfNeeded(item.id, direction: direction)
        PreviewController.shared.preview([item], onNavigate: navigatePreview)
        return true
    }

    private func beginDragSelectionSnapshot() {
        isDragSelecting = true
        if dragSnapshotItems == nil {
            dragSnapshotItems = liveFilteredItems
        }
    }

    private func applyDragSelection(_ update: DragSelectionUpdate) {
        var transaction = Transaction()
        transaction.animation = nil
        withTransaction(transaction) {
            isDragSelecting = true
            selectedIDs = update.selectedIDs
            focusedID = update.focusedID
            anchorID = update.anchorID
            dragAnchorID = update.anchorID
        }
    }

    private func endDragSelection() {
        isDragSelecting = false
        dragAnchorID = nil
        dragSnapshotItems = nil
    }

    private func rowID(at location: CGPoint) -> ClipItem.ID? {
        rowID(atViewportPoint: location)
    }

    private func rowID(atViewportPoint location: CGPoint) -> ClipItem.ID? {
        guard let scrollView = historyScrollView,
              let visibleRange = visibleContentRange(in: scrollView) else { return nil }
        let viewportSize = scrollView.contentView.bounds.size
        guard viewportSize.width > 0,
              viewportSize.height > 0,
              location.x >= historyListHorizontalInset,
              location.x <= viewportSize.width - historyListHorizontalInset,
              location.y >= toolbarHeight,
              location.y <= viewportSize.height else { return nil }

        let yInList = visibleRange.lowerBound + location.y - historyListVerticalInset
        guard yInList >= 0 else { return nil }

        let index = Int(floor(yInList / historyRowHeight))
        guard filteredItems.indices.contains(index) else { return nil }
        return filteredItems[index].id
    }

    private func edgeRowID(for location: CGPoint) -> ClipItem.ID? {
        guard let viewportSize = historyViewportSize else { return nil }

        if location.y < toolbarHeight {
            return visibleItemIndices().first.map { filteredItems[$0].id } ?? filteredItems.first?.id
        }

        if location.y > viewportSize.height {
            return visibleItemIndices().last.map { filteredItems[$0].id } ?? filteredItems.last?.id
        }

        return nil
    }

    private var historyViewportSize: CGSize? {
        guard let scrollView = historyScrollView else { return nil }
        let size = scrollView.contentView.bounds.size
        guard size.width > 0, size.height > 0 else { return nil }
        return size
    }

    private func visibleItemIDs() -> [ClipItem.ID] {
        visibleItemIndices().map { filteredItems[$0].id }
    }

    private var emptyView: some View {
        VStack(spacing: 10) {
            Image(systemName: store.items.isEmpty ? "tray" : "line.3.horizontal.decrease.circle")
                .font(.system(size: 42))
                .foregroundStyle(.secondary)
            Text(store.items.isEmpty ? "还没有记录" : "没有匹配的记录")
                .font(.system(size: 17, weight: .semibold))
            Text(store.items.isEmpty ? "复制文字或文件，或者截一张图。" : "试试其它记录类型或搜索内容。")
                .font(.subheadline)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

private struct ToolbarHeightKey: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}

/// Frosts only the titlebar strip so scrolled rows don't collide with the
/// title; below it rows stay visible under the glass controls.
private struct TitlebarScrollEdge: View {
    let height: CGFloat

    var body: some View {
        Rectangle()
            .fill(.bar)
            .mask(
                LinearGradient(
                    stops: [
                        .init(color: .black, location: 0),
                        .init(color: .black, location: 0.7),
                        .init(color: .black.opacity(0), location: 1)
                    ],
                    startPoint: .top,
                    endPoint: .bottom
                )
            )
            .frame(height: height + 18)
            .allowsHitTesting(false)
    }
}

/// Lets the custom titlebar strip move and zoom the window like a real titlebar.
private struct WindowDragHandle: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView { DragView() }
    func updateNSView(_ nsView: NSView, context: Context) {}

    final class DragView: NSView {
        override var mouseDownCanMoveWindow: Bool { true }

        override func mouseDown(with event: NSEvent) {
            if event.clickCount == 2 {
                window?.performZoom(nil)
            } else {
                window?.performDrag(with: event)
            }
        }
    }
}

private struct GlassToolbarIconButtonStyle: ButtonStyle {
    let isDimmed: Bool

    func makeBody(configuration: Configuration) -> some View {
        GlassToolbarIconButtonBody(
            label: configuration.label,
            isDimmed: isDimmed,
            isPressed: configuration.isPressed
        )
    }
}

private struct GlassToolbarIconButtonBody<Label: View>: View {
    @Environment(\.isEnabled) private var isEnabled
    @State private var isHovered = false
    let label: Label
    let isDimmed: Bool
    let isPressed: Bool

    var body: some View {
        label
            .font(.system(size: 14, weight: .medium))
            .foregroundStyle(isDimmed || !isEnabled ? Color.secondary.opacity(0.45) : Color.primary.opacity(0.82))
            .frame(width: 38, height: 36)
            .background(
                Circle()
                    .fill(Color.primary.opacity(isPressed ? 0.14 : (isHovered && !isDimmed ? 0.07 : 0)))
                    .frame(width: 32, height: 32)
            )
            .contentShape(Rectangle())
            .scaleEffect(isPressed ? 0.92 : 1)
            .animation(.spring(response: 0.22, dampingFraction: 0.7), value: isPressed)
            .animation(.easeOut(duration: 0.12), value: isHovered)
            .onHover { isHovered = $0 }
    }
}

private struct HistoryTypeFilterBar: View {
    @Binding var selection: ClipKindFilter

    var body: some View {
        LiquidSegmentedControl(
            options: ClipKindFilter.allCases,
            selection: $selection,
            title: \.title,
            segmentWidth: 64,
            height: 38
        )
        .accessibilityLabel("记录类型")
    }
}

private struct ClipRow: View {
    @Environment(\.colorScheme) private var colorScheme
    let item: ClipItem
    @ObservedObject var store: ClipStore
    let isFocused: Bool
    let isSelected: Bool
    let selectionColor: Color
    let selectionColorIsPreset: Bool
    let imageThumbnailCache: ImageThumbnailCache
    let showsSeparator: Bool
    let handleClick: (NSEvent?) -> Void
    let handleCopy: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            preview
                .frame(width: 52, height: 40)
                .clipped()

            VStack(alignment: .leading, spacing: 4) {
                Text(item.title)
                    .font(.system(size: 14, weight: .medium))
                    .lineLimit(2)
                HStack(spacing: 8) {
                    Text(kindText)
                    Text(DateText.formatter.string(from: item.createdAt))
                }
                .font(.caption)
                .foregroundStyle(isSelected ? selectedMetadataColor : Color.secondary)
            }

            Spacer()

            Button {
                store.togglePinned(item)
            } label: {
                Image(systemName: item.isPinned ? "pin.fill" : "pin")
            }
            .buttonStyle(ChatGPTIconButtonStyle(isSelected: isSelected))
            .floatingTooltip(item.isPinned ? "取消置顶" : "置顶")

            Button {
                handleCopy()
            } label: {
                Image(systemName: "doc.on.doc")
            }
            .buttonStyle(ChatGPTIconButtonStyle(isSelected: isSelected))
            .floatingTooltip(isSelected ? "复制选中的记录" : "复制")

            Button(role: .destructive) {
                store.remove(item)
            } label: {
                Image(systemName: "trash")
            }
            .buttonStyle(ChatGPTIconButtonStyle(isSelected: isSelected))
            .floatingTooltip("删除记录")
        }
        .frame(height: 58)
        .padding(.vertical, 8)
        .padding(.horizontal, 12)
        .foregroundStyle(isSelected ? selectedTextColor : Color.primary)
        .background {
            ClipRowCardSurface(
                isSelected: isSelected,
                selectionColor: selectionColor,
                selectionOpacity: SelectionRowMotion.surfaceOpacity(
                    isSelected: isSelected,
                    isDark: colorScheme == .dark,
                    isPreset: selectionColorIsPreset
                )
            )
            .animation(SelectionRowMotion.animation, value: isSelected)
        }
        .contentShape(Rectangle())
        .onTapGesture {
            handleClick(NSApp.currentEvent)
        }
        .contextMenu {
            ForEach(ClipRowMenu.orderedActions, id: \.self) { action in
                switch action {
                case .copy:
                    Button(ClipRowMenu.title(for: action)) {
                        handleCopy()
                    }
                case .pin:
                    Button(ClipRowMenu.pinTitle(isPinned: item.isPinned)) {
                        store.togglePinned(item)
                    }
                case .delete:
                    Button(ClipRowMenu.title(for: action), role: .destructive) {
                        store.remove(item)
                    }
                }
            }
        }
    }

    private var selectedTextColor: Color {
        colorScheme == .dark ? .white : .black
    }

    private var selectedMetadataColor: Color {
        colorScheme == .dark ? .white.opacity(0.72) : .black.opacity(0.72)
    }

    @ViewBuilder
    private var preview: some View {
        switch item.kind {
        case .text:
            RoundedRectangle(cornerRadius: 10)
                .fill(AppTheme.textPreviewBackground)
                .overlay(Image(systemName: "text.alignleft").foregroundStyle(AppTheme.textPreviewForeground))
        case .file:
            FileTypePreview(path: item.filePaths.first)
        case .image:
            CachedImageThumbnail(item: item, cache: imageThumbnailCache)
        }
    }

    private var kindText: String {
        switch item.kind {
        case .text: "文字"
        case .file: item.filePaths.count > 1 ? "\(item.filePaths.count) 个文件" : "文件"
        case .image: item.sourcePath == nil ? "图片" : "截图"
        }
    }
}

/// Each record is its own translucent card; the selected one becomes a tinted
/// Liquid Glass slab on macOS 26.
private struct ClipRowCardSurface: View {
    @Environment(\.colorScheme) private var colorScheme
    let isSelected: Bool
    let selectionColor: Color
    let selectionOpacity: Double

    private var shape: RoundedRectangle {
        RoundedRectangle(cornerRadius: 16, style: .continuous)
    }

    var body: some View {
        ZStack {
            shape.fill(Color.white.opacity(colorScheme == .dark ? 0.06 : 0.58))
            shape.strokeBorder(Color.white.opacity(colorScheme == .dark ? 0.08 : 0.7), lineWidth: 0.5)
            selectedSurface
                .opacity(selectionOpacity)
        }
        .padding(.vertical, 3)
    }

    @ViewBuilder
    private var selectedSurface: some View {
        if #available(macOS 26.0, *) {
            Color.clear
                .glassEffect(
                    .regular.tint(selectionColor.opacity(colorScheme == .dark ? 0.55 : 0.4)).interactive(),
                    in: shape
                )
        } else {
            shape.fill(selectionColor)
        }
    }
}

private struct CachedImageThumbnail: View {
    let item: ClipItem
    let cache: ImageThumbnailCache
    @State private var thumbnail: CGImage?

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: thumbnail == nil ? 10 : 7)
                .fill(thumbnail == nil ? AppTheme.imagePreviewBackground : AppTheme.imageThumbnailBackground)
            if let thumbnail {
                Image(decorative: thumbnail, scale: 2)
                    .resizable()
                    .scaledToFit()
                    .padding(2)
            } else {
                Image(systemName: "photo")
                    .foregroundStyle(AppTheme.imagePreviewForeground)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .task(id: item.id) {
            thumbnail = nil
            guard let data = item.imageData else { return }
            thumbnail = await cache.thumbnail(for: item.id, data: data)
        }
    }
}

private struct FileTypePreview: View {
    let path: String?
    @State private var category: FileTypeIconCategory

    init(path: String?) {
        self.path = path
        _category = State(initialValue: FileTypeIconResolver.initialCategory(for: path))
    }

    var body: some View {
        RoundedRectangle(cornerRadius: 10)
            .fill(AppTheme.fileTypeBackground(category))
            .overlay(
                Image(systemName: category.symbolName)
                    .foregroundStyle(AppTheme.fileTypeForeground(category))
            )
            .task(id: path) {
                let resolved = await FileTypeIconResolver.resolvedCategory(for: path)
                guard !Task.isCancelled else { return }
                category = resolved
            }
    }
}

private struct ChatGPTIconButtonStyle: ButtonStyle {
    let isSelected: Bool

    func makeBody(configuration: Configuration) -> some View {
        IconButtonStyleBody(
            label: configuration.label,
            isSelected: isSelected,
            isPressed: configuration.isPressed
        )
    }
}

private struct IconButtonStyleBody<Label: View>: View {
    @Environment(\.colorScheme) private var colorScheme
    @State private var isHovered = false
    let label: Label
    let isSelected: Bool
    let isPressed: Bool

    var body: some View {
        label
            .font(.system(size: 15, weight: .medium))
            .foregroundStyle(isSelected ? (colorScheme == .dark ? Color.white : Color.black) : Color.secondary)
            .frame(width: 34, height: 34)
            .scaleEffect(isPressed ? 0.9 : 1)
            .background(
                Circle()
                    .fill(
                        IconButtonAppearance.background(
                            isSelected: isSelected,
                            isPressed: isPressed,
                            isHovered: isHovered
                        )
                    )
                    .animation(.easeOut(duration: 0.10), value: isHovered)
            )
            .onHover { isHovered = $0 }
    }
}

private extension View {
    @ViewBuilder
    func clipShelfGlassGroup() -> some View {
        if #available(macOS 26.0, *) {
            padding(.horizontal, 2)
                .glassEffect(.regular.interactive(), in: Capsule())
        } else {
            padding(.horizontal, 2)
                .background(.regularMaterial, in: Capsule())
                .overlay(Capsule().stroke(AppTheme.subtleBorder, lineWidth: 0.5))
        }
    }

    @ViewBuilder
    func clipShelfGlassPanel(cornerRadius: CGFloat) -> some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
        if #available(macOS 26.0, *) {
            clipShape(shape)
                .glassEffect(.regular, in: shape)
        } else {
            background(AppTheme.settingsBackground)
                .clipShape(shape)
                .shadow(color: Color.black.opacity(0.18), radius: 24, x: 0, y: 12)
        }
    }

    @ViewBuilder
    func clipShelfLiquidGlassSurface(cornerRadius: CGFloat) -> some View {
        if #available(macOS 26.0, *) {
            glassEffect(
                .regular,
                in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
            )
        } else {
            background(AppTheme.searchBackground)
                .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
                .overlay(
                    RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                        .stroke(AppTheme.subtleBorder, lineWidth: 1)
                )
        }
    }

    @ViewBuilder
    func clipShelfLiquidGlassButtonStyle() -> some View {
        if #available(macOS 26.0, *) {
            buttonStyle(.glass)
        } else {
            buttonStyle(.borderless)
        }
    }

    func floatingTooltip(_ text: String) -> some View {
        background(FloatingTooltipAnchor(text: text))
    }
}

private struct FloatingTooltipAnchor: NSViewRepresentable {
    let text: String

    func makeNSView(context: Context) -> TooltipTrackingView {
        let view = TooltipTrackingView()
        view.text = text
        return view
    }

    func updateNSView(_ nsView: TooltipTrackingView, context: Context) {
        nsView.text = text
    }
}

private final class TooltipTrackingView: NSView {
    var text = ""
    private var trackingAreaReference: NSTrackingArea?
    private var hoverTask: DispatchWorkItem?

    override var acceptsFirstResponder: Bool { false }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingAreaReference {
            removeTrackingArea(trackingAreaReference)
        }

        let area = NSTrackingArea(
            rect: bounds,
            options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        trackingAreaReference = area
        addTrackingArea(area)
    }

    override func mouseEntered(with event: NSEvent) {
        hoverTask?.cancel()
        let task = DispatchWorkItem { [weak self] in
            guard let self, self.window != nil else { return }
            FloatingTooltipWindow.shared.show(text: self.text, for: self)
        }
        hoverTask = task
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.55, execute: task)
    }

    override func mouseExited(with event: NSEvent) {
        hoverTask?.cancel()
        FloatingTooltipWindow.shared.hide()
    }

    override func removeFromSuperview() {
        hoverTask?.cancel()
        FloatingTooltipWindow.shared.hide()
        super.removeFromSuperview()
    }
}

private final class FloatingTooltipWindow {
    static let shared = FloatingTooltipWindow()
    private var window: NSWindow?
    private let label = NSTextField(labelWithString: "")

    private init() {
        label.font = .systemFont(ofSize: 11)
        label.textColor = .white
        label.alignment = .center
        label.lineBreakMode = .byClipping
        label.maximumNumberOfLines = 1
        label.backgroundColor = .clear
    }

    func show(text: String, for sourceView: NSView) {
        guard let sourceWindow = sourceView.window else { return }
        label.stringValue = text
        let textSize = label.intrinsicContentSize
        let size = NSSize(width: textSize.width + 16, height: textSize.height + 10)
        let localRect = sourceView.convert(sourceView.bounds, to: nil)
        let screenRect = sourceWindow.convertToScreen(localRect)
        let screenFrame = sourceWindow.screen?.visibleFrame ?? NSScreen.main?.visibleFrame ?? .zero
        let prefersAbove = screenRect.minY - size.height - 8 > screenFrame.minY
        let originY = prefersAbove ? screenRect.minY - size.height - 8 : screenRect.maxY + 8
        let originX = min(max(screenRect.midX - size.width / 2, screenFrame.minX + 8), screenFrame.maxX - size.width - 8)

        let tooltipWindow = window ?? makeWindow()
        tooltipWindow.contentView = TooltipBubbleView(label: label)
        tooltipWindow.setFrame(NSRect(origin: NSPoint(x: originX, y: originY), size: size), display: true)
        tooltipWindow.orderFrontRegardless()
        window = tooltipWindow
    }

    func hide() {
        window?.orderOut(nil)
    }

    private func makeWindow() -> NSWindow {
        let window = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 80, height: 28),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        window.level = .floating
        window.backgroundColor = .clear
        window.isOpaque = false
        window.hasShadow = true
        window.ignoresMouseEvents = true
        return window
    }
}

private final class TooltipBubbleView: NSView {
    private let label: NSTextField

    init(label: NSTextField) {
        self.label = label
        super.init(frame: .zero)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.withAlphaComponent(0.88).cgColor
        layer?.cornerRadius = 7
        addSubview(label)
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func layout() {
        super.layout()
        label.frame = bounds.insetBy(dx: 8, dy: 5)
    }
}

private struct ScrollViewResolver: NSViewRepresentable {
    let onResolve: (NSScrollView?) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        DispatchQueue.main.async {
            let scrollView = resolveScrollView(from: view)
            configure(scrollView)
            onResolve(scrollView)
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        DispatchQueue.main.async {
            let scrollView = resolveScrollView(from: nsView)
            configure(scrollView)
            onResolve(scrollView)
        }
    }

    private func configure(_ scrollView: NSScrollView?) {
        guard let scrollView else { return }
        scrollView.usesPredominantAxisScrolling = true
        scrollView.verticalScrollElasticity = .automatic
        scrollView.horizontalScrollElasticity = .none
        scrollView.scrollerStyle = .overlay
        scrollView.hasVerticalScroller = true
        scrollView.autohidesScrollers = true
    }

    private func resolveScrollView(from view: NSView) -> NSScrollView? {
        if let enclosing = view.enclosingScrollView {
            return enclosing
        }

        guard let windowContent = view.window?.contentView else {
            return nil
        }

        return firstScrollView(in: windowContent)
    }

    private func firstScrollView(in view: NSView) -> NSScrollView? {
        if let scrollView = view as? NSScrollView {
            return scrollView
        }

        for subview in view.subviews {
            if let scrollView = firstScrollView(in: subview) {
                return scrollView
            }
        }

        return nil
    }
}

private struct KeyboardCaptureView: NSViewRepresentable {
    let onKeyDown: (NSEvent) -> Void

    func makeNSView(context: Context) -> KeyCaptureNSView {
        let view = KeyCaptureNSView()
        view.onKeyDown = onKeyDown
        DispatchQueue.main.async {
            view.window?.makeFirstResponder(view)
        }
        return view
    }

    func updateNSView(_ nsView: KeyCaptureNSView, context: Context) {
        nsView.onKeyDown = onKeyDown
        DispatchQueue.main.async {
            if nsView.window?.firstResponder == nil {
                nsView.window?.makeFirstResponder(nsView)
            }
        }
    }
}

private final class KeyCaptureNSView: NSView {
    var onKeyDown: ((NSEvent) -> Void)?

    override var acceptsFirstResponder: Bool { true }

    override func keyDown(with event: NSEvent) {
        if event.modifierFlags.contains(.command),
           ["a", "c"].contains(event.charactersIgnoringModifiers?.lowercased()) {
            onKeyDown?(event)
            return
        }

        switch event.keyCode {
        case 49, 51, 117, 125, 126:
            onKeyDown?(event)
        default:
            super.keyDown(with: event)
        }
    }
}

private struct DragSelectionUpdate {
    let selectedIDs: Set<ClipItem.ID>
    let focusedID: ClipItem.ID
    let anchorID: ClipItem.ID
    let lowerBound: Int
    let upperBound: Int
}

private struct DragSelectionCaptureView: NSViewRepresentable {
    let isEnabled: Bool
    let clickRecoveryDuration: TimeInterval
    let itemIDs: [ClipItem.ID]
    let rowHeight: CGFloat
    let contentTopInset: CGFloat
    var viewportTopObstruction: CGFloat = 0
    let contentHorizontalInset: CGFloat
    let onPointerDown: (CGPoint) -> Void
    let onSmallDragClick: (CGPoint, NSEvent?) -> Void
    let onDragBegan: () -> Void
    let onSelectionChanged: (DragSelectionUpdate) -> Void
    let onEnd: () -> Void
    let onCancel: () -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        context.coordinator.view = view
        context.coordinator.install()
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        context.coordinator.isEnabled = isEnabled
        context.coordinator.setClickRecoveryDuration(clickRecoveryDuration)
        context.coordinator.itemIDs = itemIDs
        context.coordinator.rowHeight = rowHeight
        context.coordinator.contentTopInset = contentTopInset
        context.coordinator.viewportTopObstruction = viewportTopObstruction
        context.coordinator.contentHorizontalInset = contentHorizontalInset
        context.coordinator.onPointerDown = onPointerDown
        context.coordinator.onSmallDragClick = onSmallDragClick
        context.coordinator.onDragBegan = onDragBegan
        context.coordinator.onSelectionChanged = onSelectionChanged
        context.coordinator.onEnd = onEnd
        context.coordinator.onCancel = onCancel
    }

    static func dismantleNSView(_ nsView: NSView, coordinator: Coordinator) {
        coordinator.uninstall()
    }

    final class Coordinator {
        var onPointerDown: ((CGPoint) -> Void)?
        var onSmallDragClick: ((CGPoint, NSEvent?) -> Void)?
        var onDragBegan: (() -> Void)?
        var onSelectionChanged: ((DragSelectionUpdate) -> Void)?
        var onEnd: (() -> Void)?
        var onCancel: (() -> Void)?
        var isEnabled = true
        var clickRecoveryDuration: TimeInterval = DragSelectionPreferences.clickRecoveryDuration
        var itemIDs: [ClipItem.ID] = []
        var rowHeight: CGFloat = 74
        var contentTopInset: CGFloat = 8
        /// Height of the floating toolbar covering the top of the viewport.
        var viewportTopObstruction: CGFloat = 0
        var contentHorizontalInset: CGFloat = 10
        weak var view: NSView?

        private var monitor: Any?
        private var observers: [NSObjectProtocol] = []
        private var startedInViewport = false
        private var didDrag = false
        private var pointerDownPoint: CGPoint?
        private var pointerDownEvent: NSEvent?
        private var hadSmallDrag = false
        private var itemIDsSnapshot: [ClipItem.ID] = []
        private var anchorIndex: Int?
        private var lastDragEvent: NSEvent?
        private var autoScrollTimer: Timer?
        private var currentScrollSpeed: CGFloat = 0
        private var lastTargetScrollSpeed: CGFloat = 0
        private var lastSelectionBounds: ClosedRange<Int>?
        private var lastLogTime = Date.distantPast
        private var lastAutoScrollFrameTime: CFTimeInterval?
        private var postDragClickRecoveryUntil = Date.distantPast
        private var pendingRecoveredClickPoint: CGPoint?
        private let dragActivationDistance: CGFloat = 6
        private let edgeZoneHeight: CGFloat = 72
        private let maxScrollSpeed: CGFloat = 900
        private let minScrollSpeed: CGFloat = 40
        private let accelerationSmoothing: CGFloat = 0.18
        private let frameInterval: TimeInterval = 1.0 / 60.0
        private let deadZone: CGFloat = 4
        private let debugDragSelection = false

        func install() {
            guard monitor == nil else { return }
            monitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .leftMouseDragged, .leftMouseUp, .scrollWheel]) { [weak self] event in
                self?.handle(event) ?? event
            }
            observers = [
                NotificationCenter.default.addObserver(
                    forName: NSApplication.didResignActiveNotification,
                    object: nil,
                    queue: .main
                ) { [weak self] _ in
                    self?.cancelDrag()
                },
                NotificationCenter.default.addObserver(
                    forName: NSWindow.didResignKeyNotification,
                    object: nil,
                    queue: .main
                ) { [weak self] _ in
                    self?.cancelDrag()
                }
            ]
        }

        func uninstall() {
            if let monitor {
                NSEvent.removeMonitor(monitor)
                self.monitor = nil
            }
            observers.forEach(NotificationCenter.default.removeObserver)
            observers.removeAll()
            cancelDrag()
        }

        func setClickRecoveryDuration(_ duration: TimeInterval) {
            clickRecoveryDuration = duration
            if duration <= 0 {
                clearPostDragClickRecovery()
            }
        }

        private func handle(_ event: NSEvent) -> NSEvent? {
            guard isEnabled else {
                cancelDrag()
                return event
            }

            guard event.window === NSApp.keyWindow else {
                cancelDrag()
                return event
            }

            guard let point = localTopLeftPoint(from: event) else {
                cancelDrag()
                return event
            }

            switch event.type {
            case .leftMouseDown:
                if isRecoveringPostDragClick {
                    pendingRecoveredClickPoint = isPointInsideViewport(point) ? point : nil
                    if pendingRecoveredClickPoint != nil {
                        onPointerDown?(point)
                        return nil
                    }
                    clearPostDragClickRecovery()
                }

                startedInViewport = isPointInsideViewport(point)
                didDrag = false
                hadSmallDrag = false
                pointerDownPoint = startedInViewport ? point : nil
                pointerDownEvent = startedInViewport ? event : nil
                if startedInViewport {
                    onPointerDown?(point)
                }
                return event
            case .leftMouseDragged:
                if isRecoveringPostDragClick {
                    pendingRecoveredClickPoint = isPointInsideViewport(point) ? point : pendingRecoveredClickPoint
                    return nil
                }

                if !startedInViewport {
                    startedInViewport = isPointInsideViewport(point)
                    didDrag = false
                    hadSmallDrag = false
                    pointerDownPoint = startedInViewport ? point : nil
                    pointerDownEvent = startedInViewport ? event : nil
                    if startedInViewport {
                        onPointerDown?(point)
                    }
                }
                guard startedInViewport else { return event }
                guard shouldActivateDrag(at: point) else {
                    hadSmallDrag = true
                    return nil
                }
                if !didDrag {
                    guard beginRangeSelection() else { return nil }
                    didDrag = true
                    onDragBegan?()
                }
                updateRangeSelection(with: event)
                return nil
            case .leftMouseUp:
                if isRecoveringPostDragClick {
                    let recoveredPoint = isPointInsideViewport(point) ? point : pendingRecoveredClickPoint
                    clearPostDragClickRecovery()
                    if let recoveredPoint {
                        onSmallDragClick?(recoveredPoint, event)
                        return nil
                    }
                    return event
                }

                guard startedInViewport else { return event }
                let shouldConsumeMouseUp = didDrag
                let shouldReplaySmallDragClick = hadSmallDrag && !didDrag
                finishDrag()
                if shouldReplaySmallDragClick {
                    onSmallDragClick?(point, event)
                    return nil
                }
                return shouldConsumeMouseUp ? nil : event
            case .scrollWheel:
                return startedInViewport || didDrag ? nil : event
            default:
                return event
            }
        }

        private func localTopLeftPoint(from event: NSEvent) -> CGPoint? {
            guard let viewport = viewportView() else { return nil }
            let pointInView = viewport.convert(event.locationInWindow, from: nil)
            let visible = viewport.bounds
            return CGPoint(
                x: pointInView.x - visible.minX,
                y: viewport.isFlipped ? pointInView.y - visible.minY : visible.maxY - pointInView.y
            )
        }

        private func isPointInsideViewport(_ point: CGPoint) -> Bool {
            guard let view = viewportView() else { return false }
            return point.x >= 0 &&
                point.y >= viewportTopObstruction &&
                point.x <= view.bounds.width &&
                point.y <= view.bounds.height
        }

        private func viewportView() -> NSView? {
            scrollView()?.contentView ?? view
        }

        private func scrollView() -> NSScrollView? {
            if let enclosing = view?.enclosingScrollView {
                return enclosing
            }

            guard let contentView = view?.window?.contentView else {
                return nil
            }

            return firstScrollView(in: contentView)
        }

        private func firstScrollView(in view: NSView) -> NSScrollView? {
            if let scrollView = view as? NSScrollView {
                return scrollView
            }

            for subview in view.subviews {
                if let scrollView = firstScrollView(in: subview) {
                    return scrollView
                }
            }

            return nil
        }

        private func beginRangeSelection() -> Bool {
            guard !itemIDs.isEmpty,
                  let event = pointerDownEvent else {
                return false
            }

            itemIDsSnapshot = itemIDs
            guard let index = rowIndex(from: event, clamped: false) else {
                itemIDsSnapshot = []
                return false
            }

            anchorIndex = index
            lastDragEvent = event
            startAutoScrollTimer()
            updateSelection(hoverIndex: index, event: event, didScroll: false)
            return true
        }

        private func updateRangeSelection(with event: NSEvent) {
            guard didDrag else { return }
            lastDragEvent = event
            updateSelectionFromEvent(event, didScroll: false)
        }

        private func updateSelection(hoverIndex: Int, event: NSEvent, didScroll: Bool) {
            guard let anchorIndex,
                  itemIDsSnapshot.indices.contains(anchorIndex),
                  itemIDsSnapshot.indices.contains(hoverIndex) else { return }

            let bounds = min(anchorIndex, hoverIndex)...max(anchorIndex, hoverIndex)
            guard bounds != lastSelectionBounds else {
                logDragState(hoverIndex: hoverIndex, bounds: bounds, didScroll: didScroll, event: event)
                return
            }

            lastSelectionBounds = bounds
            let selectedIDs = Set(bounds.map { itemIDsSnapshot[$0] })
            let update = DragSelectionUpdate(
                selectedIDs: selectedIDs,
                focusedID: itemIDsSnapshot[hoverIndex],
                anchorID: itemIDsSnapshot[anchorIndex],
                lowerBound: bounds.lowerBound,
                upperBound: bounds.upperBound
            )
            onSelectionChanged?(update)
            logDragState(hoverIndex: hoverIndex, bounds: bounds, didScroll: didScroll, event: event)
        }

        private func updateSelectionFromEvent(_ event: NSEvent, didScroll: Bool) {
            guard let hoverIndex = rowIndex(from: event, clamped: true) else {
                return
            }

            updateSelection(hoverIndex: hoverIndex, event: event, didScroll: didScroll)
        }

        private func rowIndex(from event: NSEvent, clamped: Bool) -> Int? {
            guard !itemIDsSnapshot.isEmpty,
                  let mouseY = documentY(from: event) else { return nil }

            let rawIndex = Int(floor((mouseY - contentTopInset) / rowHeight))
            if clamped {
                return min(max(rawIndex, 0), itemIDsSnapshot.count - 1)
            }

            guard itemIDsSnapshot.indices.contains(rawIndex),
                  let point = localTopLeftPoint(from: event),
                  point.x >= contentHorizontalInset,
                  let viewport = viewportView(),
                  point.x <= viewport.bounds.width - contentHorizontalInset else {
                return nil
            }

            return rawIndex
        }

        private func documentY(from event: NSEvent) -> CGFloat? {
            guard let scrollView = scrollView(),
                  let documentView = scrollView.documentView,
                  let point = localTopLeftPoint(from: event) else { return nil }

            let visible = scrollView.contentView.bounds
            if documentView.isFlipped {
                return visible.minY + point.y
            }

            let documentHeight = documentView.bounds.height
            let visibleTop = max(0, documentHeight - visible.maxY)
            return visibleTop + point.y
        }

        private func startAutoScrollTimer() {
            guard autoScrollTimer == nil else { return }

            lastAutoScrollFrameTime = CACurrentMediaTime()
            let timer = Timer(timeInterval: frameInterval, repeats: true) { [weak self] _ in
                guard let self, let event = self.lastDragEvent else { return }
                self.performAutoScrollFrame(with: event)
            }
            autoScrollTimer = timer
            RunLoop.main.add(timer, forMode: .common)
            RunLoop.main.add(timer, forMode: .eventTracking)
        }

        private func stopAutoScroll() {
            autoScrollTimer?.invalidate()
            autoScrollTimer = nil
            lastDragEvent = nil
            lastAutoScrollFrameTime = nil
            currentScrollSpeed = 0
            lastTargetScrollSpeed = 0
        }

        private func performAutoScrollFrame(with event: NSEvent) {
            guard let scrollView = scrollView(),
                  let documentView = scrollView.documentView else { return }

            let targetSpeed = targetScrollSpeed(for: event, in: scrollView)
            lastTargetScrollSpeed = targetSpeed
            currentScrollSpeed += (targetSpeed - currentScrollSpeed) * accelerationSmoothing
            let deltaTime = autoScrollDeltaTime()

            if abs(currentScrollSpeed) < 0.5, targetSpeed == 0 {
                currentScrollSpeed = 0
                updateSelectionFromEvent(event, didScroll: false)
                return
            }

            let clipView = scrollView.contentView
            let visible = clipView.bounds
            let documentHeight = documentView.bounds.height
            let maxY = max(0, documentHeight - visible.height)
            let proposedY = min(max(visible.origin.y + currentScrollSpeed * deltaTime, 0), maxY)
            let didScroll = proposedY != visible.origin.y

            if didScroll {
                clipView.scroll(to: NSPoint(x: visible.origin.x, y: proposedY))
                scrollView.reflectScrolledClipView(clipView)
            } else if proposedY == 0 || proposedY == maxY {
                currentScrollSpeed = 0
            }

            updateSelectionFromEvent(event, didScroll: didScroll)
        }

        private func autoScrollDeltaTime() -> CGFloat {
            let now = CACurrentMediaTime()
            defer { lastAutoScrollFrameTime = now }
            guard let lastAutoScrollFrameTime else {
                return CGFloat(frameInterval)
            }

            let elapsed = now - lastAutoScrollFrameTime
            return CGFloat(min(max(elapsed, 1.0 / 120.0), 1.0 / 45.0))
        }

        private func targetScrollSpeed(for event: NSEvent, in scrollView: NSScrollView) -> CGFloat {
            guard let mouseY = documentY(from: event) else { return 0 }

            let visibleRect = scrollView.contentView.bounds
            let topEdge = visibleRect.minY + viewportTopObstruction
            let bottomEdge = visibleRect.maxY

            if mouseY < topEdge + edgeZoneHeight {
                let distanceToEdge = max(mouseY - topEdge, 0)
                return -scrollSpeed(distanceToEdge: distanceToEdge)
            }

            if mouseY > bottomEdge - edgeZoneHeight {
                let distanceToEdge = max(bottomEdge - mouseY, 0)
                return scrollSpeed(distanceToEdge: distanceToEdge)
            }

            return 0
        }

        private func scrollSpeed(distanceToEdge: CGFloat) -> CGFloat {
            let rawPenetration = min(max(1 - distanceToEdge / edgeZoneHeight, 0), 1)
            let deadZonePenetration = deadZone / edgeZoneHeight
            guard rawPenetration > deadZonePenetration else { return 0 }

            let penetration = (rawPenetration - deadZonePenetration) / (1 - deadZonePenetration)
            let eased = penetration * penetration
            return minScrollSpeed + eased * (maxScrollSpeed - minScrollSpeed)
        }

        private func logDragState(
            hoverIndex: Int,
            bounds: ClosedRange<Int>,
            didScroll: Bool,
            event: NSEvent
        ) {
            #if DEBUG
            guard debugDragSelection else { return }
            let now = Date()
            guard now.timeIntervalSince(lastLogTime) >= 0.15 else { return }
            lastLogTime = now

            guard let scrollView = scrollView(),
                  let documentView = scrollView.documentView,
                  let mouseY = documentY(from: event) else { return }

            let visibleRect = documentView.visibleRect
            let clipOriginY = scrollView.contentView.bounds.origin.y
            NSLog(
                "ClipShelf drag-select visibleMinY=\(visibleRect.minY) visibleMaxY=\(visibleRect.maxY) mouseY=\(mouseY) anchorIndex=\(anchorIndex ?? -1) hoverIndex=\(hoverIndex) selection=\(bounds.lowerBound)...\(bounds.upperBound) clipOriginY=\(clipOriginY) targetSpeed=\(lastTargetScrollSpeed) currentSpeed=\(currentScrollSpeed) didScroll=\(didScroll)"
            )
            #endif
        }

        private func finishDrag() {
            let wasTracking = startedInViewport || didDrag
            let wasDrag = didDrag
            stopAutoScroll()
            startedInViewport = false
            didDrag = false
            pointerDownPoint = nil
            pointerDownEvent = nil
            hadSmallDrag = false
            itemIDsSnapshot = []
            anchorIndex = nil
            lastSelectionBounds = nil
            if wasDrag {
                beginPostDragClickRecovery()
            }
            if wasTracking {
                onEnd?()
            }
        }

        private func cancelDrag() {
            let wasTracking = startedInViewport || didDrag
            let wasDrag = didDrag
            stopAutoScroll()
            startedInViewport = false
            didDrag = false
            pointerDownPoint = nil
            pointerDownEvent = nil
            hadSmallDrag = false
            itemIDsSnapshot = []
            anchorIndex = nil
            lastSelectionBounds = nil
            if wasDrag {
                beginPostDragClickRecovery()
            }
            if wasTracking {
                onCancel?()
            }
        }

        private func shouldActivateDrag(at point: CGPoint) -> Bool {
            guard let pointerDownPoint else { return true }
            let deltaX = point.x - pointerDownPoint.x
            let deltaY = point.y - pointerDownPoint.y
            return hypot(deltaX, deltaY) >= dragActivationDistance
        }

        private var isRecoveringPostDragClick: Bool {
            Date() < postDragClickRecoveryUntil
        }

        private func beginPostDragClickRecovery() {
            guard clickRecoveryDuration > 0 else {
                clearPostDragClickRecovery()
                return
            }

            postDragClickRecoveryUntil = Date().addingTimeInterval(clickRecoveryDuration)
            pendingRecoveredClickPoint = nil
        }

        private func clearPostDragClickRecovery() {
            postDragClickRecoveryUntil = .distantPast
            pendingRecoveredClickPoint = nil
        }
    }
}
