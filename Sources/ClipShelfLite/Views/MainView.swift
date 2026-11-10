import AppKit
import SwiftUI

struct MainView: View {
    static let minimumSize = CGSize(width: 680, height: 552)

    private struct SearchSignature: Equatable {
        let revision: Int
        let kind: ClipKindFilter
        let query: String
    }

    @ObservedObject var store: ClipStore
    @ObservedObject var watcher: ScreenshotFolderWatcher
    @State private var searchText = ""
    @State private var kindFilter: ClipKindFilter = HistoryFilterPreferences.value
    @State private var selectedIDs = Set<ClipItem.ID>()
    @State private var focusedID: ClipItem.ID?
    @State private var anchorID: ClipItem.ID?
    @State private var isDragSelecting = false
    /// The range being drag-selected; read only by DragRangeHighlight (a
    /// plain reference in @State, so this view doesn't observe it).
    @State private var dragHighlight = DragHighlightModel()
    /// Which row the pointer is over: one hover tracker for the whole list
    /// instead of four per row (each was updated on every scrolled frame).
    @State private var rowHover = RowHoverModel()
    @State private var pendingDragSelection: DragSelectionUpdate?
    @State private var dragSnapshotItems: [ClipItem]?
    @State private var historyScrollView: NSScrollView?
    @State private var selectionColor = SelectionColorPreferences.color
    @State private var selectionColorPreset = SelectionColorPreferences.selectedPreset
    @State private var switchToClickedRecord = SelectionClickBehaviorPreferences.switchToClickedRecord
    @State private var multiSelectionClickSelectedBehavior = SelectionClickBehaviorPreferences.multiSelectionClickSelectedBehavior
    @State private var multiSelectionClickUnselectedBehavior = SelectionClickBehaviorPreferences.multiSelectionClickUnselectedBehavior
    @State private var postDragClickRecoveryDuration = DragSelectionPreferences.clickRecoveryDuration
    @State private var commandKeyMonitor: Any?
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
    @State private var filteredItemIDsCache = [ClipItem.ID]()
    @State private var filteredItemsSignature: SearchSignature?
    @State private var pendingFilterSignature: SearchSignature?
    @State private var searchIndexCache = ClipSearchIndexCache()
    @State private var imageThumbnailCache = ImageThumbnailCache()
    @FocusState private var isSearchFocused: Bool
    /// Height of the title row and the type / action bar.
    @State private var toolbarHeight: CGFloat = 100
    /// The search row exists (laid out below the action bar).
    @State private var isSearchVisible = false
    /// The search glass has poured out of its button to full width.
    @State private var isSearchExpanded = false
    @State private var searchButtonFrame: CGRect = .zero
    /// Width of the toolbar row, which the search field spans when open.
    @State private var toolbarRowWidth: CGFloat = 0
    /// While the search glass moves it shows a look-alike placeholder
    /// instead of the native text field, which is costly to animate.
    @State private var isSearchFieldLive = false
    @State private var searchMotionGeneration = 0
    @State private var toggleSearchHotKey = ToggleSearchHotKeyDefaults.load()
    @State private var listEntrance: ListEntrance?
    @State private var listEntranceStyle = ListEntrancePreferences.value
    @State private var lastKindFilter: ClipKindFilter = HistoryFilterPreferences.value
    @State private var filteredIndexByID = [ClipItem.ID: Int]()
    @State private var glassTransparency = GlassPreferences.value
    @State private var scrollEffect = ScrollEffectPreferences.value
    @State private var copyToast: CopyToast?
    @State private var titlebarHeight: CGFloat = 28
    /// Bumps at midnight, so rows reading "今天" turn into "昨天".
    @State private var dayToken = 0
    private let historyRowHeight: CGFloat = 74
    private let historyListHorizontalInset: CGFloat = GlassMetrics.edgeInset
    private let historyListBottomPadding: CGFloat = 16
    /// Cards draw 3pt inside their row slot (see ClipRow.card).
    private let rowCardInset: CGFloat = 3

    /// The glass toolbar floats over the top of the list, so rows start below it
    /// and every viewport calculation treats that strip as covered.
    /// Height of the custom titlebar row (traffic lights, icon, title).
    private var titleRowHeight: CGFloat { max(titlebarHeight, 30) }

    /// Height of the search row plus its gap.
    private var searchRowDistance: CGFloat {
        GlassMetrics.controlHeight + GlassMetrics.stackSpacing
    }

    /// Height of the toolbar as it covers the list. The search field floats
    /// over the list like Spotlight, so it never pushes records down.
    private var visibleToolbarHeight: CGFloat {
        toolbarHeight
    }

    /// The toolbar's clickable strip, including the floating search field.
    private var toolbarHitHeight: CGFloat {
        toolbarHeight + (isSearchVisible ? searchRowDistance : 0)
    }

    private var historyListVerticalInset: CGFloat {
        visibleToolbarHeight + GlassMetrics.stackSpacing - rowCardInset
    }

    private var searchSignature: SearchSignature {
        SearchSignature(revision: store.revision, kind: kindFilter, query: searchText)
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
                    .padding(.top, visibleToolbarHeight)
            } else {
                // Runs to the window's lower edge, where the window's own
                // rounded clip cuts everything off (glass included). The
                // list's own clip doesn't stop system glass: ending the list
                // above the edge left a strip of card glass below it.
                historyList
            }

            toolbar
                .background(
                    GeometryReader { proxy in
                        Color.clear.preference(key: ToolbarHeightKey.self, value: proxy.size.height)
                    }
                )
                // Read right here, not around the list: there every row took
                // part in collecting it on each scrolled frame.
                .onPreferenceChange(ToolbarHeightKey.self) { height in
                    let rounded = height.rounded()
                    if abs(rounded - toolbarHeight) > 0.5 {
                        toolbarHeight = rounded
                    }
                }
        }
        .ignoresSafeArea(.container, edges: .top)
        .overlay(alignment: .bottom) {
            if let copyToast {
                CopyToastView(count: copyToast.count)
                    .padding(.bottom, 24)
                    .id(copyToast.id)
                    .transition(
                        AccessibilityLook.current.reduceMotion
                            ? .opacity
                            : .asymmetric(
                                insertion: .move(edge: .bottom).combined(with: .scale(scale: 0.85)).combined(with: .opacity),
                                removal: .opacity.combined(with: .scale(scale: 0.95))
                            )
                    )
                    .allowsHitTesting(false)
            }
        }
        .frame(minWidth: Self.minimumSize.width, minHeight: Self.minimumSize.height)
        .environment(\.glassTransparency, glassTransparency)
        .clipShelfAccessibilityLook()
        .preferredColorScheme(resolvedColorScheme)
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
            imageThumbnailCache.setMaximumEntryCount(thumbnailCacheLimit)
            lastRevealedNewest = HistoryScrollTarget.newestDate(in: store.items)
            installCommandKeyMonitorIfNeeded()
            updateChecker.checkIfNeeded()
            SettingsWindowController.shared.configure(store: store, watcher: watcher)
            // Build the settings window ahead of time, off screen, so the first
            // open is instant and has a snapshot to animate.
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) {
                if let hostWindow {
                    SettingsWindowController.shared.prepare(parent: hostWindow)
                }
            }
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
        .onReceive(NotificationCenter.default.publisher(for: ListEntrancePreferences.changedNotification)) { notification in
            listEntranceStyle = notification.object as? ListEntranceStyle ?? ListEntrancePreferences.value
        }
        .onReceive(NotificationCenter.default.publisher(for: ToggleSearchHotKeyDefaults.changedNotification)) { notification in
            toggleSearchHotKey = notification.object as? HotKeyConfiguration ?? ToggleSearchHotKeyDefaults.load()
        }
        .onReceive(NotificationCenter.default.publisher(for: ScrollEffectPreferences.changedNotification)) { notification in
            scrollEffect = notification.object as? ScrollEffectStyle ?? ScrollEffectPreferences.value
        }
        .onReceive(NotificationCenter.default.publisher(for: GlassPreferences.changedNotification)) { notification in
            // Applied at once: the window cross-fades to the new look (see
            // GlassPreferences), which needs the change in the same frame.
            glassTransparency = notification.object as? GlassTransparency ?? GlassPreferences.value
        }
        // One O(1) change signal instead of re-scanning the history on every render.
        .onChange(of: store.revision) { _ in
            refreshFilteredItemsIfNeeded()
            prewarmSearchIndexes(for: store.items)
            imageThumbnailCache.setMaximumEntryCount(thumbnailCacheLimit)
        }
        .onChange(of: searchText) { _ in
            refreshFilteredItemsIfNeeded()
            clearSelection()
        }
        .onChange(of: kindFilter) { _ in
            startListEntrance(from: lastKindFilter, to: kindFilter)
            lastKindFilter = kindFilter
            // Back to the top before the new rows are laid out, so only the
            // top screenful is built; scrolling afterwards built the rows at
            // the old position first and then the top ones again.
            scrollHistoryToTop()
            refreshFilteredItemsIfNeeded()
            clearSelection()
            HistoryFilterPreferences.value = kindFilter
        }
        // The selection stays when the app goes to the background.
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
        .onReceive(NotificationCenter.default.publisher(for: ClearSelectionHotKeyDefaults.changedNotification)) { notification in
            clearSelectionHotKey = notification.object as? HotKeyConfiguration ?? ClearSelectionHotKeyDefaults.load()
        }
        .onReceive(NotificationCenter.default.publisher(for: PinHotKeyDefaults.changedNotification)) { notification in
            pinHotKey = notification.object as? HotKeyConfiguration ?? PinHotKeyDefaults.load()
        }
        .onReceive(NotificationCenter.default.publisher(for: .NSCalendarDayChanged).receive(on: RunLoop.main)) { _ in
            dayToken &+= 1
        }
    }

    private var historyList: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(filteredItems) { item in
                        let isSelected = selectedIDs.contains(item.id)
                        let index = filteredIndexByID[item.id] ?? 0
                        let isFocused = focusedID == item.id
                        RowHoverGate(model: rowHover, id: item.id) { isHovered in
                            ClipRow(
                                item: item,
                                store: store,
                                isFocused: isFocused,
                                isSelected: isSelected,
                                isHovered: isHovered,
                                dayToken: dayToken,
                                selectionColor: selectionColor,
                                selectionColorIsPreset: selectionColorPreset != nil,
                                imageThumbnailCache: imageThumbnailCache,
                                handleClick: { event in handleRowClick(item, event: event) },
                                handleCopy: { performCopy(actionItems(for: item)) }
                            )
                            .equatable()
                        }
                        // Rows are a fixed height; saying so stops the lazy
                        // stack from measuring every row on every scroll frame.
                        .modifier(FixedRowHeight(height: historyRowHeight))
                        // No edge blur on the rows themselves: inside the
                        // former shared glass container it never showed, and
                        // the list's look is kept as it was.
                        .modifier(ListEntranceEffect(entrance: listEntrance, index: index, rowHeight: historyRowHeight))
                        .id(item.id)
                    }
                }
                // The stack keeps its identity across type switches: rebuilding
                // it (and fading the old one out) cost about twice the work of
                // just swapping the rows; on-screen rows replay their entrance.
                // No shared GlassEffectContainer around the cards: with one,
                // every scrolled frame re-resolved all cards' glass together
                // (6 s of fast scrolling dropped ~70 frames; without, ~5).
                .background(alignment: .top) {
                    SelectionGlassLayer(
                        items: filteredItems,
                        selectedIDs: selectedIDs,
                        panes: selectionPanes,
                        rowHeight: historyRowHeight,
                        selectionColor: selectionColor,
                        scrollEffect: scrollEffect,
                        topEdge: visibleToolbarHeight,
                        isDragging: isDragSelecting
                    )
                }
                .overlay(alignment: .top) {
                    DragRangeHighlight(model: dragHighlight, rowHeight: historyRowHeight, inset: rowCardInset)
                }
                .padding(.horizontal, historyListHorizontalInset)
                .padding(.top, historyListVerticalInset)
                .padding(.bottom, historyListBottomPadding)
            }
            .coordinateSpace(name: "historyList")
            // No fade along the bottom: the cards' system glass ignores a
            // fading mask, so their text and rim faded while the glass body
            // stayed whole and the two looked split apart.
            .mask {
                VStack(spacing: 0) {
                    Color.black
                        // Nothing of the list behind the open search field:
                        // card glass renders above ordinary views, so their
                        // text showed through the field.
                        .overlay(alignment: .top) {
                            Capsule()
                                .frame(height: GlassMetrics.controlHeight)
                                .padding(.horizontal, GlassMetrics.edgeInset)
                                .padding(.top, toolbarHeight + GlassMetrics.stackSpacing)
                                // Only once the field has poured out: cut
                                // earlier, the empty full-width hole showed
                                // as a grey bar ahead of the animation.
                                .opacity(isSearchExpanded && isSearchFieldLive ? 1 : 0)
                                .animation(.easeOut(duration: 0.15), value: isSearchFieldLive)
                                .blendMode(.destinationOut)
                        }
                        .compositingGroup()
                }
            }
            .background(
                ScrollViewResolver { scrollView in
                    historyScrollView = scrollView
                    if let scrollView { GlassScroller.install(in: scrollView) }
                }
            )
            // Hover comes from one AppKit tracking area over the list: it
            // only does work when the pointer moves, never per scrolled frame.
            .background(
                RowHoverTracker(scrollView: historyScrollView) { point in
                    rowHover.set(point.flatMap { rowID(atViewportPoint: $0) })
                }
            )
            .background(KeyboardCaptureView { event in
                handleKey(event, scrollProxy: proxy)
            })
            .background(
                DragSelectionCaptureView(
                    isEnabled: true,
                    clickRecoveryDuration: postDragClickRecoveryDuration,
                    itemIDs: filteredItemIDs,
                    rowHeight: historyRowHeight,
                    contentTopInset: historyListVerticalInset,
                    viewportTopObstruction: visibleToolbarHeight,
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
            // Only scrolling needs the proxy; all state observers live on the
            // root view so they keep working while the list is empty.
            .onChange(of: store.revision) { _ in
                revealNewestItemIfNeeded(proxy: proxy)
            }
        }
    }

    private var toolbar: some View {
        VStack(spacing: 4) {
            titleRow

            HStack(spacing: 12) {
                // Outside any glass container: its lens is its own glass.
                LiquidSegmentedControl(
                    options: ClipKindFilter.allCases,
                    selection: $kindFilter,
                    title: \.title,
                    segmentWidth: 64,
                    height: GlassMetrics.controlHeight,
                    followsGlassSlider: true
                )

                Spacer(minLength: 8)
                // The toolbar buttons and the search field are one body of
                // glass: the field melts out of its button and back into it.
                mergingGlass {
                    primaryToolbarButtons
                        // The search field floats below the toolbar as its
                        // own glass layer, over the list, like Spotlight:
                        // records stay where they are. It spans the whole
                        // toolbar row, out to the left of the buttons.
                        .overlay(alignment: .bottomTrailing) {
                            if isSearchVisible {
                                searchRow
                                    .frame(width: max(toolbarRowWidth, 1))
                                    .offset(y: searchRowDistance)
                            }
                        }
                }
            }
            .background(
                GeometryReader { proxy in
                    Color.clear
                        .onAppear { toolbarRowWidth = proxy.size.width }
                        .onChange(of: proxy.size.width) { toolbarRowWidth = $0 }
                }
            )
        }
        .coordinateSpace(name: ToolbarSpace.name)
        .padding(.horizontal, GlassMetrics.edgeInset)
        // Gaps between controls must not click through to rows hidden
        // underneath; once collapsed, the freed strip is clickable again.
        .contentShape(TopStrip(height: toolbarHitHeight))
        .background(alignment: .top) {
            // Solid behind the title row only, then a short fade, so the
            // records' glass shows again as soon as possible.
            TitlebarScrollEdge(height: titleRowHeight, toolbarHeight: toolbarHeight)
        }
    }

    /// Lives in the (transparent) titlebar strip, next to the traffic lights.
    private var titleRow: some View {
        HStack(spacing: 8) {
            GlassTrafficLights(window: hostWindow)
                .padding(.trailing, 6)

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
        .frame(height: titleRowHeight)
        .background(WindowDragHandle())
    }

    private static let searchPlaceholder = "搜索文字、文件名、截图名"

    /// The search field pours out of the search button: its glass grows from
    /// the button's circle down into a full-width capsule (and back).
    private var searchRow: some View {
        let height = GlassMetrics.controlHeight
        return GeometryReader { proxy in
            let row = proxy.frame(in: .named(ToolbarSpace.name))
            let start = CGRect(
                x: searchButtonFrame.minX - row.minX,
                y: searchButtonFrame.minY - row.minY,
                width: max(searchButtonFrame.width, height),
                height: height
            )
            let target = CGRect(x: 0, y: 0, width: proxy.size.width, height: height)
            let rect = isSearchExpanded ? target : start
            searchField
                .frame(width: rect.width, height: rect.height)
                .offset(x: rect.minX, y: rect.minY)
                // Tucking back in, the glass melts into the button at the
                // end instead of vanishing on top of it.
                .modifier(ScopedOpacity(
                    opacity: isSearchExpanded ? 1 : 0,
                    animation: isSearchExpanded ? .easeOut(duration: 0.1) : .easeOut(duration: 0.26)
                ))
        }
        .frame(height: height)
    }

    private var searchField: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(.secondary)
            if isSearchFieldLive || !searchText.isEmpty {
                TextField(Self.searchPlaceholder, text: $searchText)
                    .textFieldStyle(.plain)
                    .font(.system(size: 14))
                    .focused($isSearchFocused)
            } else {
                Text(Self.searchPlaceholder)
                    .font(.system(size: 14))
                    .foregroundStyle(Color(nsColor: .placeholderTextColor))
                    .lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
                    .onTapGesture {
                        isSearchFieldLive = true
                        DispatchQueue.main.async { isSearchFocused = true }
                    }
            }
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
                .buttonStyle(PressFeedbackButtonStyle())
                .floatingTooltip("清除搜索")
            }
        }
        .padding(.horizontal, 14)
        // Contents appear once the glass has poured out.
        .opacity(isSearchExpanded ? 1 : 0)
        .animation(.easeOut(duration: isSearchExpanded ? 0.18 : 0.08).delay(isSearchExpanded ? 0.24 : 0), value: isSearchExpanded)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        // The same glass as the toolbar buttons. The list is masked out
        // behind it (see historyList), so no record text shows through.
        // Applied to the field itself, not as a background: inside the
        // toolbar's glass container a background glass is drawn over the
        // text and bends it into ghost copies.
        // Clipped first, so while the glass is still growing or shrinking
        // the text never spills out past it.
        .clipShape(Capsule())
        .contentShape(Capsule())
        .clipShelfGlass(in: Capsule(), followsSlider: true)
        // Lifted off the list, like Spotlight.
        .shadow(color: .black.opacity(isSearchExpanded ? 0.22 : 0), radius: 16, y: 8)
    }

    /// Opens the search row by pouring it out of its button, or tucks it back.
    private func setSearchVisible(_ visible: Bool, focus: Bool = true) {
        if visible, isSearchVisible, isSearchExpanded {
            if focus {
                isSearchFieldLive = true
                DispatchQueue.main.async { isSearchFocused = true }
            }
            return
        }
        guard visible || isSearchVisible else { return }
        searchMotionGeneration += 1
        let generation = searchMotionGeneration
        var plain = Transaction()
        plain.disablesAnimations = true
        let motion = Animation.clipShelf(.spring(response: 0.46, dampingFraction: 0.8))
        if visible {
            withTransaction(plain) {
                isSearchFieldLive = false
                isSearchVisible = true
            }
            DispatchQueue.main.async {
                guard generation == searchMotionGeneration else { return }
                withAnimation(motion) {
                    isSearchExpanded = true
                }
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.42) {
                guard generation == searchMotionGeneration, isSearchVisible else { return }
                withTransaction(plain) { isSearchFieldLive = true }
                if focus {
                    DispatchQueue.main.async { isSearchFocused = true }
                }
            }
        } else {
            // Keyboard focus leaves only once the field is gone: resigning it
            // re-renders the window and used to stall the first frames.
            let hadFocus = isSearchFocused
            if !searchText.isEmpty { searchText = "" }
            // No bounce on the way back: it settles into the button.
            withAnimation(.clipShelf(.spring(response: 0.4, dampingFraction: 0.92))) {
                isSearchExpanded = false
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
                guard generation == searchMotionGeneration, !isSearchExpanded else { return }
                withTransaction(plain) {
                    // Swapped only once hidden, so no placeholder flashes.
                    isSearchFieldLive = false
                    isSearchVisible = false
                }
                if hadFocus, let window = NSApp.keyWindow, window.firstResponder is NSText {
                    window.makeFirstResponder(nil)
                }
            }
        }
    }

    /// Glass shapes inside melt together when they come within 8pt of each
    /// other (the toolbar groups sit 10pt apart, the open search field 10pt
    /// below its button, so at rest they stay apart).
    @ViewBuilder
    private func mergingGlass<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        if #available(macOS 26.0, *) {
            GlassEffectContainer(spacing: 8) { content() }
        } else {
            content()
        }
    }

    /// Two glass capsules, like system toolbar groups: places, then actions.
    private var primaryToolbarButtons: some View {
        let hasNoActionItems = actionItems.isEmpty
        return HStack(spacing: 10) {
            toolbarButton("magnifyingglass", tooltip: "搜索（\(toggleSearchHotKey.displayText)）", isActive: isSearchVisible && isSearchExpanded) {
                setSearchVisible(!isSearchVisible)
            }
            .clipShelfGlassGroup(followsSlider: true)
            // Where the search field pours out of.
            .background(
                GeometryReader { proxy in
                    Color.clear
                        .onAppear { searchButtonFrame = proxy.frame(in: .named(ToolbarSpace.name)) }
                        .onChange(of: proxy.frame(in: .named(ToolbarSpace.name))) { searchButtonFrame = $0 }
                }
            )

            HStack(spacing: 0) {
                toolbarButton("folder", tooltip: "添加截图文件夹") {
                    debugFolderPickerLog("choose folder button clicked")
                    watcher.chooseFolder(attachedTo: hostWindow)
                }
                toolbarButton("gearshape", tooltip: "设置") {
                    toggleSettings()
                }
                .onHover { inside in
                    guard inside else { return }
                    let controller = SettingsWindowController.shared
                    if controller.isVisible {
                        controller.prefetchClosingSnapshot()
                    } else {
                        controller.prefetchOpeningSnapshot(for: hostWindow)
                    }
                }
                // Where the settings window pours out of and collapses back into.
                .background(
                    GeometryReader { proxy in
                        Color.clear
                            .onAppear { SettingsWindowController.shared.anchorInWindow = proxy.frame(in: .global) }
                            .onChange(of: proxy.frame(in: .global)) { frame in
                                SettingsWindowController.shared.anchorInWindow = frame
                            }
                    }
                )
            }
            .clipShelfGlassGroup(followsSlider: true)

            HStack(spacing: 0) {
                toolbarButton("doc.on.doc", tooltip: copyActionHelp, isDimmed: hasNoActionItems) {
                    copyActionItems()
                }
                toolbarButton("pin", tooltip: "置顶选中的记录", isDimmed: hasNoActionItems) {
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
            .clipShelfGlassGroup(followsSlider: true)
        }
    }

    private func toolbarButton(
        _ systemImage: String,
        tooltip: String,
        isDimmed: Bool = false,
        isActive: Bool = false,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Image(systemName: systemImage)
        }
        .buttonStyle(GlassToolbarIconButtonStyle(isDimmed: isDimmed, isActive: isActive))
        .floatingTooltip(tooltip)
    }

    private func openSettings() {
        SettingsWindowController.shared.show(from: hostWindow)
    }

    private func toggleSettings() {
        SettingsWindowController.shared.toggle(from: hostWindow)
    }

    private func refreshFilteredItemsIfNeeded() {
        let signature = searchSignature
        guard filteredItemsSignature != signature, pendingFilterSignature != signature else { return }

        let items = store.items
        let kind = kindFilter
        let query = searchText
        let indexCache = searchIndexCache

        // Small histories and plain type filters are instant; only text search
        // over a large history moves off the main thread.
        let isHeavySearch = !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            && items.count >= 200
        guard isHeavySearch else {
            pendingFilterSignature = nil
            applyFilteredItems(
                ClipHistoryFilter.items(items, kind: kind, query: query, indexCache: indexCache),
                signature: signature
            )
            return
        }

        pendingFilterSignature = signature
        DispatchQueue.global(qos: .userInitiated).async {
            let result = ClipHistoryFilter.items(items, kind: kind, query: query, indexCache: indexCache)
            DispatchQueue.main.async {
                // A newer keystroke supersedes this result; keep the current list until it lands.
                guard pendingFilterSignature == signature else { return }
                pendingFilterSignature = nil
                applyFilteredItems(result, signature: signature)
            }
        }
    }

    private func applyFilteredItems(_ items: [ClipItem], signature: SearchSignature) {
        filteredItemsCache = items
        filteredItemIDsCache = items.map(\.id)
        var indexByID = [ClipItem.ID: Int](minimumCapacity: items.count)
        for (index, item) in items.enumerated() {
            indexByID[item.id] = index
        }
        filteredIndexByID = indexByID
        filteredItemsSignature = signature
        // Records that are gone (deleted from their own row, pushed out by
        // the history limit) leave the selection too; they used to be
        // counted as selected still.
        guard dragSnapshotItems == nil else { return }
        if !selectedIDs.isEmpty {
            let remaining = selectedIDs.filter { indexByID[$0] != nil }
            if remaining.count != selectedIDs.count { selectedIDs = remaining }
        }
        if let focused = focusedID, indexByID[focused] == nil { focusedID = nil }
        if let anchor = anchorID, indexByID[anchor] == nil { anchorID = nil }
    }

    private func startListEntrance(from old: ClipKindFilter, to new: ClipKindFilter) {
        // Reduce Motion: the new list simply appears.
        guard listEntranceStyle != .off, old != new, !AccessibilityLook.current.reduceMotion else {
            listEntrance = nil
            return
        }
        let cases = ClipKindFilter.allCases
        let oldIndex = cases.firstIndex(of: old) ?? 0
        let newIndex = cases.firstIndex(of: new) ?? 0
        // The type bar's lens, relative to the centre of the list (64pt
        // segments inside a 4pt inset, starting at the list's left edge).
        let listWidth = max((hostWindow?.frame.width ?? 704) - historyListHorizontalInset * 2, 200)
        let lensX = 4 + 64 * (CGFloat(newIndex) + 0.5)
        listEntrance = ListEntrance(
            style: listEntranceStyle,
            direction: newIndex >= oldIndex ? 1 : -1,
            lensOffsetX: lensX - listWidth / 2,
            start: Date()
        )
    }

    /// Where the selection glass goes. A single selection keeps one stable
    /// pane that slides from record to record; several each get their own.
    private var selectionPanes: [SelectionGlassPane] {
        let indices = selectedIndices
        if indices.count == 1, let index = indices.first {
            return [SelectionGlassPane(id: "single", index: index)]
        }
        return indices.map { SelectionGlassPane(id: filteredItems[$0].id.uuidString, index: $0) }
    }

    /// Where the selected records are in the list, top to bottom. Looked up
    /// from the selection, not by going through the whole history.
    private var selectedIndices: [Int] {
        guard !selectedIDs.isEmpty else { return [] }
        // The index table describes the live list; a drag works on a snapshot.
        guard dragSnapshotItems == nil else {
            return filteredItems.indices.filter { selectedIDs.contains(filteredItems[$0].id) }
        }
        return selectedIDs.compactMap { filteredIndexByID[$0] }.sorted()
    }

    private var selectedItems: [ClipItem] {
        let items = filteredItems
        return selectedIndices.map { items[$0] }
    }

    private var filteredItemIDs: [ClipItem.ID] {
        dragSnapshotItems?.map(\.id) ?? filteredItemIDsCache
    }

    /// Thumbnails are ~33 KB each; keep enough for fast scrolling, not all history.
    private var thumbnailCacheLimit: Int {
        min(store.items.count + 8, 400)
    }

    private func revealNewestItemIfNeeded(proxy: ScrollViewProxy) {
        guard !isDragSelecting,
              let newID = HistoryScrollTarget.anchorToReveal(
                in: store.items,
                lastRevealedNewest: lastRevealedNewest
              ) else { return }
        withAnimation(.easeOut(duration: 0.18)) {
            proxy.scrollTo(newID, anchor: .top)
        }
        lastRevealedNewest = HistoryScrollTarget.newestDate(in: store.items)
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
            togglePreviewOfSelection()
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

            let isPlainEscape = event.keyCode == 53
                && event.modifierFlags.intersection([.command, .option, .control, .shift]).isEmpty
            // Settings open: Esc belongs to settings (it closes it).
            if isPlainEscape, SettingsWindowController.shared.isVisible {
                return event
            }
            // Esc closes an open search field before clearing the selection.
            if isPlainEscape, isSearchVisible {
                setSearchVisible(false)
                return nil
            }

            if clearSelectionHotKey.matches(event) {
                clearSelection()
                return nil
            }

            if pinHotKey.matches(event) {
                togglePinnedForActionItems()
                return nil
            }

            if toggleSearchHotKey.matches(event) {
                setSearchVisible(!isSearchVisible)
                return nil
            }

            if event.keyCode == 53 {
                if isSearchVisible {
                    setSearchVisible(false)
                    return nil
                }
                if !searchText.isEmpty {
                    clearSearchState()
                    return nil
                }
            }

            // Space previews, whatever has keyboard focus in the window (the
            // focus view lost it after settings had been open).
            if event.keyCode == 49,
               event.modifierFlags.intersection([.command, .option, .control, .shift]).isEmpty,
               !isSearchFocused {
                togglePreviewOfSelection()
                return nil
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
        guard let window = event.window,
              window === NSApp.keyWindow,
              window.title.hasPrefix("ClipShelf"),
              // Settings covers the list; its keys belong to settings.
              !SettingsWindowController.shared.isVisible else {
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
            performCopy(actionItems)
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
        let selected = selectedItems
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
            let selected = selectedItems
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
        performCopy(actionItems)
    }

    /// Every copy goes through here so it always confirms with a toast.
    private func performCopy(_ items: [ClipItem]) {
        guard !items.isEmpty else { return }
        store.copy(items)
        let toast = CopyToast(count: items.count)
        withAnimation(.clipShelf(.spring(response: 0.42, dampingFraction: 0.72))) {
            copyToast = toast
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.6) {
            guard copyToast?.id == toast.id else { return }
            withAnimation(.easeInOut(duration: 0.3)) {
                copyToast = nil
            }
        }
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
        let visibleTop = visibleToolbarHeight + topPadding
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

        let first = max(0, Int(floor((range.lowerBound + visibleToolbarHeight - historyListVerticalInset) / historyRowHeight)))
        let last = min(filteredItems.count - 1, Int(floor((range.upperBound - historyListVerticalInset) / historyRowHeight)))
        guard first <= last else { return [] }

        return Array(first...last)
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

    /// Where a record's card is on screen (for the preview to grow from).
    private func rowRectOnScreen(_ item: ClipItem) -> NSRect? {
        guard let index = filteredIndexByID[item.id],
              let scrollView = historyScrollView,
              let document = scrollView.documentView,
              let window = scrollView.window else { return nil }
        let top = historyListVerticalInset + CGFloat(index) * historyRowHeight + rowCardInset
        let height = historyRowHeight - rowCardInset * 2
        let width = document.bounds.width - historyListHorizontalInset * 2
        let y = document.isFlipped ? top : document.bounds.height - top - height
        let rect = NSRect(x: historyListHorizontalInset, y: y, width: width, height: height)
        let inWindow = document.convert(rect, to: nil)
        // Only if the record is actually in view.
        guard scrollView.convert(scrollView.bounds, to: nil).intersects(inWindow) else { return nil }
        return window.convertToScreen(inWindow)
    }

    private var previewItems: [ClipItem] {
        let selected = selectedItems
        if selected.count == 1 {
            return selected
        }

        if selected.count > 1 {
            // Several selected: start at the newest, whichever was clicked
            // last; ↓ then steps to older ones within the selection.
            return previewOrder(selected).first.map { [$0] } ?? []
        }

        if let focusedItem {
            return [focusedItem]
        }

        return []
    }

    /// Newest first.
    private func previewOrder(_ items: [ClipItem]) -> [ClipItem] {
        items.sorted { $0.createdAt > $1.createdAt }
    }

    private func togglePreviewOfSelection() {
        let items = previewItems
        guard let item = items.first else { return }
        focusedID = item.id
        PreviewController.shared.togglePreview(items, from: rowRectOnScreen(item), onNavigate: navigatePreview)
    }

    private func navigatePreview(direction: Int) -> Bool {
        guard !filteredItems.isEmpty else { return false }

        // Several selected: step only between them (newest to oldest),
        // keeping the selection.
        let selected = previewOrder(selectedItems)
        if selected.count > 1 {
            guard let currentID = focusedID,
                  let position = selected.firstIndex(where: { $0.id == currentID }) else { return false }
            let next = min(max(position + direction, 0), selected.count - 1)
            guard next != position else { return false }
            let item = selected[next]
            focusedID = item.id
            revealKeyboardSelectionIfNeeded(item.id, direction: direction)
            PreviewController.shared.preview([item], from: rowRectOnScreen(item), onNavigate: navigatePreview)
            return true
        }

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
        PreviewController.shared.preview([item], from: rowRectOnScreen(item), onNavigate: navigatePreview)
        return true
    }

    private func beginDragSelectionSnapshot() {
        isDragSelecting = true
        if dragSnapshotItems == nil {
            dragSnapshotItems = liveFilteredItems
        }
        // The drag picks a new range: the old selection goes now, once.
        var transaction = Transaction()
        transaction.disablesAnimations = true
        withTransaction(transaction) {
            selectedIDs = []
        }
    }

    /// While dragging, only the light range highlight follows the pointer
    /// (its own small model, so the window and its rows don't re-render on
    /// every row crossed, which dropped a frame each time). The selection
    /// itself is committed when the drag ends.
    private func applyDragSelection(_ update: DragSelectionUpdate) {
        pendingDragSelection = update
        dragHighlight.show(update.lowerBound...update.upperBound)
    }

    /// On release the selection is committed the way a Shift-click range
    /// is: each newly selected record's glass springs up in place, while the
    /// drag highlight fades out over it.
    private func endDragSelection() {
        // No longer dragging first, so the new panes play their pop.
        isDragSelecting = false
        if let update = pendingDragSelection {
            withAnimation(SelectionRowMotion.animation) {
                selectedIDs = update.selectedIDs
                focusedID = update.focusedID
                anchorID = update.anchorID
            }
        }
        pendingDragSelection = nil
        dragSnapshotItems = nil
        let highlight = dragHighlight
        guard highlight.range != nil else { return }
        let token = highlight.beginFade()
        withAnimation(.easeOut(duration: 0.25)) { highlight.fadeOut(token) }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { highlight.clear(token) }
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
              location.y >= visibleToolbarHeight,
              location.y <= viewportSize.height else { return nil }

        let yInList = visibleRange.lowerBound + location.y - historyListVerticalInset
        guard yInList >= 0 else { return nil }

        let index = Int(floor(yInList / historyRowHeight))
        guard filteredItems.indices.contains(index) else { return nil }
        return filteredItems[index].id
    }

    private var emptyView: some View {
        let content = emptyStateContent
        return VStack(spacing: 10) {
            Image(systemName: content.symbol)
                .font(.system(size: 42))
                .foregroundStyle(.secondary)
            Text(content.title)
                .font(.system(size: 17, weight: .semibold))
            Text(content.message)
                .font(.subheadline)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    /// Says why the list is empty: no history at all, no records of the chosen
    /// type, or no search results.
    private var emptyStateContent: (symbol: String, title: String, message: String) {
        if store.items.isEmpty {
            return ("tray", "还没有记录", "复制文字或文件，或者截一张图。")
        }
        let isSearching = !searchText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        if !isSearching, kindFilter != .all {
            let hint: String
            switch kindFilter {
            case .file: hint = "在访达里复制文件后，会出现在这里。"
            case .image: hint = "复制图片或截图后，会出现在这里。"
            case .text, .all: hint = "复制文字后，会出现在这里。"
            }
            return ("tray", "还没有\(kindFilter.title)记录", hint)
        }
        return ("magnifyingglass", "没有匹配的记录", "试试其它关键词或记录类型。")
    }
}

/// Gives a row its known height without asking the row to measure itself.
private struct FixedRowHeight: ViewModifier {
    let height: CGFloat

    func body(content: Content) -> some View {
        FixedHeightLayout(height: height) { content }
    }
}

private struct FixedHeightLayout: Layout {
    let height: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        CGSize(width: proposal.width ?? 0, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        for subview in subviews {
            subview.place(at: bounds.origin, proposal: ProposedViewSize(bounds.size))
        }
    }
}

/// The top `height` points of a view, for hit testing.
private struct TopStrip: Shape {
    var height: CGFloat

    func path(in rect: CGRect) -> Path {
        // Not clamped: the search row hangs below the toolbar's own bounds.
        Path(CGRect(x: rect.minX, y: rect.minY, width: rect.width, height: height))
    }
}

private struct CopyToast: Equatable {
    let id = UUID()
    let count: Int
}

/// "已复制" / "已复制 N 条" confirmation floating above the bottom of the list.
private struct CopyToastView: View {
    let count: Int

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "checkmark.circle.fill")
                .font(.system(size: 16, weight: .semibold))
                .foregroundStyle(.green)
            Text(count > 1 ? "已复制 \(count) 条" : "已复制")
                .font(.system(size: 14, weight: .semibold))
        }
        .padding(.horizontal, 18)
        .padding(.vertical, 11)
        .clipShelfGlass(in: Capsule())
        .shadow(color: .black.opacity(0.14), radius: 14, y: 6)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.updatesFrequently)
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
/// Close / minimise / zoom as tinted Liquid Glass beads, replacing the
/// system's buttons (hidden in AppDelegate). They keep their colour while
/// ClipShelf is the active app, also while settings has keyboard focus or
/// is closing; only when another app is in front do they turn grey.
private struct GlassTrafficLights: View {
    let window: NSWindow?
    @Environment(\.colorScheme) private var colorScheme
    @State private var isAppActive = NSApp.isActive
    @State private var isHovered = false

    private let size: CGFloat = 14

    var body: some View {
        HStack(spacing: 8) {
            light(Color(red: 1, green: 0.37, blue: 0.34), symbol: "xmark") { window?.performClose(nil) }
            light(Color(red: 1, green: 0.74, blue: 0.18), symbol: "minus") { window?.miniaturize(nil) }
            light(Color(red: 0.16, green: 0.79, blue: 0.25), symbol: "plus") { window?.zoom(nil) }
        }
        .onHover { inside in
            withAnimation(.easeOut(duration: 0.12)) { isHovered = inside }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            withAnimation(.easeOut(duration: 0.2)) { isAppActive = true }
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didResignActiveNotification)) { _ in
            withAnimation(.easeOut(duration: 0.2)) { isAppActive = false }
        }
    }

    private func light(_ color: Color, symbol: String, action: @escaping () -> Void) -> some View {
        let isDark = colorScheme == .dark
        let lit = isAppActive || isHovered
        let liquid = lit ? color : Color(white: isDark ? 0.42 : 0.78)
        return Button(action: action) {
            ZStack {
                // Flat coloured glass: one even tint, no highlight or gradient.
                Circle().fill(liquid.opacity(0.88))
                Image(systemName: symbol)
                    .font(.system(size: 7.5, weight: .heavy))
                    .foregroundStyle(Color.black.opacity(0.5))
                    .opacity(isHovered ? 1 : 0)
            }
            .frame(width: size, height: size)
            // Clear system glass around it: its rim bends what's behind.
            .modifier(TrafficLightGlass())
            .contentShape(Circle())
        }
        .buttonStyle(TrafficLightPress())
    }
}

private struct TrafficLightGlass: ViewModifier {
    @Environment(\.accessibilityLook) private var look

    func body(content: Content) -> some View {
        if look.reduceTransparency {
            content
        } else if #available(macOS 26.0, *) {
            content.glassEffect(.clear, in: Circle())
        } else {
            content
        }
    }
}

private struct TrafficLightPress: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .pressFeedback(configuration.isPressed)
    }
}

/// Animates only the opacity with its own curve, leaving the frame to the
/// surrounding transaction (older systems: plain opacity).
private struct ScopedOpacity: ViewModifier {
    let opacity: Double
    let animation: Animation

    func body(content: Content) -> some View {
        if #available(macOS 14.0, *) {
            content.animation(animation) { $0.opacity(opacity) }
        } else {
            content.opacity(opacity)
        }
    }
}

private struct TitlebarScrollEdge: View {
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityLook) private var look
    let height: CGFloat
    /// The whole toolbar, title row and controls.
    var toolbarHeight: CGFloat = 0
    private let fade: CGFloat = 40

    /// Dark: the deep grey of an inactive titlebar. Light: a soft grey a
    /// little darker than the white controls, so they stand out on it.
    private var shade: Color {
        colorScheme == .dark
            ? Color(.sRGB, red: 47 / 255, green: 50 / 255, blue: 51 / 255)
            : Color(.sRGB, red: 0.9, green: 0.9, blue: 0.91)
    }

    var body: some View {
        if look.reduceTransparency {
            // Reduce Transparency: the whole toolbar band is solid, so no
            // record shows between the controls, with only a short fade.
            let solidHeight = max(toolbarHeight, height)
            SolidSurface.window(isDark: colorScheme == .dark)
                .mask(
                    LinearGradient(
                        stops: [
                            .init(color: .black, location: 0),
                            .init(color: .black, location: solidHeight / (solidHeight + 8)),
                            .init(color: .black.opacity(0), location: 1)
                        ],
                        startPoint: .top,
                        endPoint: .bottom
                    )
                )
                .frame(height: solidHeight + 8)
                .allowsHitTesting(false)
        } else {
            glassEdge
        }
    }

    private var glassEdge: some View {
        let total = max(height + fade, 1)
        let solid = Double(height / total)
        let tail = 1 - solid
        return ZStack {
            Rectangle().fill(.bar)
            Rectangle().fill(shade.opacity(0.9))
        }
        // Solid behind the toolbar, then an eased fade (no visible edge).
        .mask(
            LinearGradient(
                stops: [
                    .init(color: .black, location: 0),
                    .init(color: .black, location: solid),
                    .init(color: .black.opacity(0.8), location: solid + tail * 0.2),
                    .init(color: .black.opacity(0.5), location: solid + tail * 0.45),
                    .init(color: .black.opacity(0.22), location: solid + tail * 0.7),
                    .init(color: .black.opacity(0.06), location: solid + tail * 0.9),
                    .init(color: .black.opacity(0), location: 1)
                ],
                startPoint: .top,
                endPoint: .bottom
            )
        )
        .frame(height: total)
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
                window?.dragWindow(with: event)
            }
        }
    }
}

private struct GlassToolbarIconButtonStyle: ButtonStyle {
    let isDimmed: Bool
    var isActive = false

    func makeBody(configuration: Configuration) -> some View {
        GlassToolbarIconButtonBody(
            label: configuration.label,
            isDimmed: isDimmed,
            isActive: isActive,
            isPressed: configuration.isPressed
        )
    }
}

private struct GlassToolbarIconButtonBody<Label: View>: View {
    @Environment(\.isEnabled) private var isEnabled
    @State private var isHovered = false
    let label: Label
    let isDimmed: Bool
    var isActive = false
    let isPressed: Bool

    var body: some View {
        label
            .font(.system(size: 14, weight: isActive ? .semibold : .medium))
            .foregroundStyle(isDimmed || !isEnabled ? Color.secondary.opacity(0.45) : (isActive ? Color.accentColor : Color.primary.opacity(0.82)))
            .animation(.easeOut(duration: 0.2), value: isActive)
            .frame(width: 38, height: GlassMetrics.controlHeight)
            .background(
                Circle()
                    .fill(Color.primary.opacity(isHovered && !isDimmed ? 0.07 : 0))
                    .frame(width: 32, height: 32)
            )
            .contentShape(Rectangle())
            .pressFeedback(isPressed)
            .animation(.easeOut(duration: 0.12), value: isHovered)
            .onHover { isHovered = $0 }
    }
}

/// Equatable so a row only re-renders when its own record or selection state
/// changes, not on every store update or unrelated selection change.
private struct ClipRow: View, Equatable {
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.accessibilityLook) private var look
    /// The light spreading from the last click (see `PressRipple`).
    @State private var ripple: PressRippleSeed?
    let item: ClipItem
    /// Not observed: the parent already re-renders rows whose data changed.
    let store: ClipStore
    let isFocused: Bool
    let isSelected: Bool
    /// From the list's single hover tracker.
    let isHovered: Bool
    /// Changes at midnight: the row's date text is relative to today.
    let dayToken: Int
    let selectionColor: Color
    let selectionColorIsPreset: Bool
    let imageThumbnailCache: ImageThumbnailCache
    let handleClick: (NSEvent?) -> Void
    let handleCopy: () -> Void

    static func == (lhs: ClipRow, rhs: ClipRow) -> Bool {
        lhs.item.id == rhs.item.id
            && lhs.item.title == rhs.item.title
            && lhs.item.isPinned == rhs.item.isPinned
            && lhs.item.kind == rhs.item.kind
            && lhs.item.createdAt == rhs.item.createdAt
            && lhs.item.filePaths == rhs.item.filePaths
            && lhs.item.sourcePath == rhs.item.sourcePath
            && lhs.isFocused == rhs.isFocused
            && lhs.isSelected == rhs.isSelected
            && lhs.isHovered == rhs.isHovered
            && lhs.dayToken == rhs.dayToken
            && lhs.selectionColor == rhs.selectionColor
            && lhs.selectionColorIsPreset == rhs.selectionColorIsPreset
            && lhs.imageThumbnailCache === rhs.imageThumbnailCache
    }

    private var face: some View {
        HStack(spacing: 12) {
            preview
                .frame(width: 52, height: 40)
                .clipped()

            VStack(alignment: .leading, spacing: 4) {
                Text(item.rowTitle)
                    .font(.system(size: 14, weight: .medium))
                    .lineLimit(2)
                HStack(spacing: 8) {
                    Text(kindText)
                    Text(DateText.relative(item.createdAt))
                }
                .font(.caption)
                .foregroundStyle(isSelected ? selectedMetadataColor : Color.secondary)
            }

            Spacer()

            RowIconAction(
                systemImage: item.isPinned ? "pin.fill" : "pin",
                label: item.isPinned ? "取消置顶" : "置顶",
                isSelected: isSelected,
                hoverEnabled: isHovered,
                // Pinned reads by colour, not by a heavier glyph.
                tint: item.isPinned ? .orange : nil,
                tooltip: item.isPinned ? "取消置顶" : "置顶"
            ) {
                store.togglePinned(item)
            }

            RowIconAction(systemImage: "doc.on.doc", label: "复制", isSelected: isSelected, hoverEnabled: isHovered, tooltip: isSelected ? "复制选中的记录" : "复制") {
                handleCopy()
            }

            RowIconAction(systemImage: "trash", label: "删除记录", isSelected: isSelected, hoverEnabled: isHovered, tooltip: "删除记录") {
                store.remove(item)
            }
        }
    }

    var body: some View {
        card
            .contentShape(Rectangle())
            .onTapGesture(coordinateSpace: .local) { location in
                handleClick(NSApp.currentEvent)
                startRipple(at: location)
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

    private var card: some View {
        // The row's content lives inside its card of system glass (68pt
        // tall, 3pt inside the 74pt row slot); while selected, the shared
        // selection glass behind takes its place.
        face
            .frame(height: 58)
            .padding(.vertical, 5)
            .padding(.horizontal, 12)
            .foregroundStyle(isSelected ? selectedTextColor : Color.primary)
            .cardGlass(
                isHovered: isHovered,
                isHidden: SelectionRowMotion.surfaceOpacity(
                    isSelected: isSelected,
                    isDark: colorScheme == .dark,
                    isPreset: selectionColorIsPreset
                ) > 0,
                fixedHeight: 68
            )
            .animation(SelectionRowMotion.animation, value: isSelected)
            .overlay {
                if let ripple {
                    PressRipple(seed: ripple)
                        .id(ripple.id)
                        .clipShape(RoundedRectangle(cornerRadius: GlassMetrics.cardRadius, style: .continuous))
                }
            }
            .padding(.vertical, 3)
    }

    /// `location` is in the row's space, which has 3pt above the card.
    private func startRipple(at location: CGPoint) {
        guard !look.reduceMotion else { return }
        let seed = PressRippleSeed(origin: CGPoint(x: location.x, y: location.y - 3))
        ripple = seed
        DispatchQueue.main.asyncAfter(deadline: .now() + PressRipple.duration + 0.05) {
            if ripple == seed { ripple = nil }
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
            RoundedRectangle(cornerRadius: GlassMetrics.thumbnailRadius, style: .continuous)
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
        case .text: "文本"
        case .file: item.filePaths.count > 1 ? "\(item.filePaths.count) 个文件" : "文件"
        case .image: item.sourcePath == nil ? "图片" : "截图"
        }
    }
}

private struct SelectionGlassPane: Identifiable, Equatable {
    let id: String
    let index: Int
}

/// The selection glass, drawn once for the whole list behind the rows. Rows
/// have a fixed height, so a pane's position follows from its index: changing
/// the selection always slides the glass from where it was to where it goes,
/// even when the previous record has scrolled out of view. Each pane gets the
/// same perspective fold as the record under it, so it hugs curved rows.
private struct SelectionGlassLayer: View {
    let items: [ClipItem]
    let selectedIDs: Set<ClipItem.ID>
    let panes: [SelectionGlassPane]
    let rowHeight: CGFloat
    let selectionColor: Color
    let scrollEffect: ScrollEffectStyle
    let topEdge: CGFloat
    /// Drag-selecting: panes appear at once, without springs or fades.
    var isDragging = false
    /// Records that were already selected before the latest change; panes
    /// for them don't pop again (e.g. when scrolled back into view).
    @State private var settled = Set<ClipItem.ID>()

    var body: some View {
        Group {
            if panes.count > 1 {
                // Many selected: a lazy stack, so only on-screen panes are drawn
                // (selecting a hundred records used to build a hundred glass panes).
                let first = panes.first?.index ?? 0
                LazyVStack(spacing: 0) {
                    ForEach(Array(items.enumerated()), id: \.element.id) { index, item in
                        if selectedIDs.contains(item.id) {
                            // The same glass as a single selection (halo included).
                            SelectionGlassSlab(selectionColor: selectionColor)
                                .modifier(SelectionPop(
                                    animates: !isDragging && !settled.contains(item.id),
                                    delay: min(Double(max(index - first, 0)) * 0.025, 0.3)
                                ))
                                .frame(height: rowHeight)
                                .transition(.opacity)
                        } else {
                            Color.clear.frame(height: rowHeight)
                        }
                    }
                }
            } else {
                // One selected: a single pane that slides from record to record.
                GeometryReader { proxy in
                    ForEach(panes) { pane in
                        SelectionGlassSlab(selectionColor: selectionColor)
                            .frame(width: proxy.size.width, height: rowHeight)
                            // Grows out of the record itself: scaled before
                            // it is positioned, so it pops in place.
                            .modifier(SelectionPop(animates: !isDragging && !selectedIDs.isSubset(of: settled)))
                            .position(x: proxy.size.width / 2, y: CGFloat(pane.index) * rowHeight + rowHeight / 2)
                            .transition(.opacity)
                    }
                }
            }
        }
        // Moves between records slide; deselected panes fade out.
        .animation(isDragging ? nil : SelectionRowMotion.animation, value: panes)
        .allowsHitTesting(false)
        .onChange(of: selectedIDs) { settled = $0 }
        .onAppear { settled = selectedIDs }
    }

    // No edge blur on the panes: the rows themselves have none, and any
    // blur at all turns system glass into a flat grey slab, so a selected
    // record's glass looked blurred well before its text did.
}

/// A newly selected record's glass springs up from the record itself.
private struct SelectionPop: ViewModifier {
    let animates: Bool
    /// When several pop at once, a short wait each so they ripple down
    /// instead of all springing in the same frame.
    var delay: Double = 0
    @State private var shown: Bool

    init(animates: Bool, delay: Double = 0) {
        self.animates = animates
        self.delay = delay
        _shown = State(initialValue: !animates)
    }

    func body(content: Content) -> some View {
        content
            .scaleEffect(shown || AccessibilityLook.current.reduceMotion ? 1 : 0.94)
            .opacity(shown ? 1 : 0)
            .onAppear {
                guard !shown else { return }
                // Softer and a little slower: a gentle swell, not a snap.
                withAnimation(.clipShelf(.spring(response: 0.46, dampingFraction: 0.74)).delay(delay)) {
                    shown = true
                }
            }
    }
}

/// A lifted pane of tinted Liquid Glass with a soft specular rim.
private struct SelectionGlassSlab: View {
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.displayScale) private var displayScale
    @Environment(\.glassTransparency) private var transparency
    @Environment(\.accessibilityLook) private var look
    let selectionColor: Color
    /// Records use the card radius; the search field passes a capsule's.
    var cornerRadius: CGFloat = GlassMetrics.cardRadius
    var verticalInset: CGFloat = 3

    private var shape: RoundedRectangle {
        RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
    }

    var body: some View {
        // Exactly the card's size (a slight enlargement made the pane
        // visibly longer than the cards in a wide window).
        surface
            .padding(.vertical, verticalInset)
    }

    @ViewBuilder
    private var surface: some View {
        let isDark = colorScheme == .dark
        // Specular highlight on top, melting into the accent colour below.
        // Dark mode is kept calmer: a softer rim and tint, so the pane
        // doesn't glare against the dark cards.
        let rim = LinearGradient(
            colors: [
                Color.white.opacity(isDark ? 0.3 : 0.95),
                Color.accentColor.opacity(isDark ? 0.22 : 0.3),
                Color.accentColor.opacity(isDark ? 0.45 : 0.6)
            ],
            startPoint: .top,
            endPoint: .bottom
        )
        if look.reduceTransparency {
            // Solid: the card colour with the selection colour over it.
            shape.fill(SolidSurface.card(isDark: isDark))
                .overlay(shape.fill(selectionColor.opacity(isDark ? 0.45 : 0.3)))
                .overlay(shape.strokeBorder(selectionColor.opacity(look.increaseContrast ? 1 : 0.7), lineWidth: look.increaseContrast ? 2 : 1.2))
        } else if #available(macOS 26.0, *) {
            Color.clear
                .glassEffect(
                    .regular.tint(selectionColor.opacity(isDark ? 0.38 : 0.34)),
                    in: shape
                )
                // No drop shadow: under translucent glass it shows through and
                // turns the whole pane grey.
                .background(glowImage)
                // Rim and top sheen, one cached image.
                .overlay(rimStroke(rim))
                // Increase Contrast: a clear outline in the selection colour.
                .overlay {
                    if look.increaseContrast {
                        shape.strokeBorder(selectionColor, lineWidth: 2)
                    }
                }
        } else {
            shape.fill(.ultraThinMaterial)
                .overlay(shape.fill(selectionColor.opacity(isDark ? 0.38 : 0.34)))
                .background(glowImage)
                .overlay(rimStroke(rim))
        }
    }

    /// The soft accent glow around the pane, blurred once into an image (a
    /// live blur was redrawn every frame the pane moved and stuttered).
    private var glowImage: some View {
        GeometryReader { proxy in
            let pad = SelectionGlowImage.padding
            if let image = SelectionGlowImage.image(
                size: proxy.size,
                cornerRadius: cornerRadius,
                isDark: colorScheme == .dark,
                scale: displayScale
            ) {
                Image(decorative: image, scale: displayScale)
                    .offset(x: -pad, y: -pad)
            }
        }
        .allowsHitTesting(false)
    }

    /// The gradient rim as a ready-made image. Drawn live, SwiftUI painted
    /// the gradient stroke on the CPU (and, cached on the GPU instead, redrew
    /// it every frame) while the pane slid, then waited for the render
    /// server: clicks stuttered. An image only moves.
    private func rimStroke(_ rim: LinearGradient) -> some View {
        GeometryReader { proxy in
            if let image = SelectionRimImage.image(
                size: proxy.size,
                cornerRadius: cornerRadius,
                isDark: colorScheme == .dark,
                scale: displayScale,
                rim: rim
            ) {
                Image(decorative: image, scale: displayScale)
            }
        }
        .allowsHitTesting(false)
    }
}

/// Rendered rims of the selection pane, by size and look.
@MainActor
private enum SelectionGlowImage {
    static let padding: CGFloat = 14
    private static var cache: [String: CGImage] = [:]

    static func image(size: CGSize, cornerRadius: CGFloat, isDark: Bool, scale: CGFloat) -> CGImage? {
        guard size.width >= 1, size.height >= 1 else { return nil }
        let accent = NSColor.controlAccentColor.usingColorSpace(.sRGB)?.description ?? ""
        let key = "\(Int(size.width.rounded()))x\(Int(size.height.rounded()))-\(cornerRadius)-\(isDark)-\(scale)-\(accent)"
        if let cached = cache[key] { return cached }
        let renderer = ImageRenderer(
            content: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                .stroke(Color.accentColor.opacity(isDark ? 0.5 : 0.4), lineWidth: 5)
                .blur(radius: 6)
                .frame(width: size.width.rounded(), height: size.height.rounded())
                .padding(padding)
        )
        renderer.scale = scale
        guard let image = renderer.cgImage else { return nil }
        if cache.count > 24 { cache.removeAll() }
        cache[key] = image
        return image
    }
}

@MainActor
private enum SelectionRimImage {
    private static var cache: [String: CGImage] = [:]

    static func image(size: CGSize, cornerRadius: CGFloat, isDark: Bool, scale: CGFloat, rim: LinearGradient) -> CGImage? {
        guard size.width >= 1, size.height >= 1 else { return nil }
        let accent = NSColor.controlAccentColor.usingColorSpace(.sRGB)?.description ?? ""
        let key = "\(Int(size.width.rounded()))x\(Int(size.height.rounded()))-\(cornerRadius)-\(isDark)-\(scale)-\(accent)"
        if let cached = cache[key] { return cached }
        let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
        let renderer = ImageRenderer(
            content: ZStack {
                // A soft white sheen just inside the top edge: the glass's
                // thickness.
                shape
                    .inset(by: 1.5)
                    .stroke(
                        LinearGradient(
                            stops: [
                                .init(color: Color.white.opacity(isDark ? 0.28 : 0.6), location: 0),
                                .init(color: Color.white.opacity(0), location: 0.35)
                            ],
                            startPoint: .top,
                            endPoint: .bottom
                        ),
                        lineWidth: 2.5
                    )
                    .blur(radius: 1)
                    .clipShape(shape)
                shape.strokeBorder(rim, lineWidth: 1.2)
            }
            .frame(width: size.width.rounded(), height: size.height.rounded())
            .environment(\.colorScheme, isDark ? .dark : .light)
        )
        renderer.scale = scale
        guard let image = renderer.cgImage else { return nil }
        // A handful of sizes at most (window widths, the search field).
        if cache.count > 24 { cache.removeAll() }
        cache[key] = image
        return image
    }
}

private struct CachedImageThumbnail: View {
    let item: ClipItem
    let cache: ImageThumbnailCache
    @State private var thumbnail: CGImage?

    init(item: ClipItem, cache: ImageThumbnailCache) {
        self.item = item
        self.cache = cache
        _thumbnail = State(initialValue: cache.cachedThumbnail(for: item.id))
    }

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: thumbnail == nil ? 10 : 7)
                .fill(thumbnail == nil ? AppTheme.imagePreviewBackground : AppTheme.imageThumbnailBackground)
            if let thumbnail {
                // Fill the tile and crop the centre: wide screenshots used to
                // shrink to a thin strip with empty space above and below.
                Image(decorative: thumbnail, scale: 2)
                    .resizable()
                    .scaledToFill()
                    .frame(width: 52, height: 40)
                    .clipped()
            } else {
                Image(systemName: "photo")
                    .foregroundStyle(AppTheme.imagePreviewForeground)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .task(id: item.id) {
            if let cached = cache.cachedThumbnail(for: item.id) {
                if thumbnail !== cached { thumbnail = cached }
                return
            }
            thumbnail = nil
            let decoded: CGImage?
            if let url = item.imageURL {
                decoded = await cache.thumbnail(for: item.id, url: url)
            } else if let data = item.legacyImageData {
                decoded = await cache.thumbnail(for: item.id, data: data)
            } else {
                return
            }
            guard !Task.isCancelled else { return }
            thumbnail = decoded
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

/// Row action icon. Deliberately not a `Button`: every button joins SwiftUI's
/// keyboard focus loop, which was rebuilt each time rows scrolled into view.
/// Keyboard users already have shortcuts for copy, pin and delete.
private struct RowIconAction: View {
    @Environment(\.colorScheme) private var colorScheme
    @State private var isHovered = false
    @State private var isPressed = false
    let systemImage: String
    let label: String
    let isSelected: Bool
    /// Only the row under the pointer tracks hover on its buttons; the
    /// others carry no hover regions to update while the list scrolls.
    let hoverEnabled: Bool
    var tint: Color? = nil
    /// Shown on hover. Handled by this view's own hover tracking rather than
    /// a separate tooltip modifier: one hover region per button, not two.
    var tooltip: String? = nil
    let action: () -> Void

    var body: some View {
        Image(systemName: systemImage)
            .font(.system(size: 15, weight: .medium))
            .foregroundStyle(tint ?? (isSelected ? (colorScheme == .dark ? Color.white : Color.black) : Color.secondary))
            .frame(width: 34, height: 34)
            .background(
                Circle()
                    .fill(IconButtonAppearance.background(isSelected: isSelected, isPressed: false, isHovered: isHovered))
            )
            .contentShape(Circle())
            .pressFeedback(isPressed)
            .animation(.easeOut(duration: 0.1), value: isHovered)
            .animation(.clipShelf(.spring(response: 0.2, dampingFraction: 0.7)), value: isPressed)
            .modifier(OptionalHover(isEnabled: hoverEnabled) { inside in
                setHovered(inside)
            })
            .onChange(of: hoverEnabled) { enabled in
                if !enabled { setHovered(false) }
            }
            .onTapGesture {
                isPressed = false
                action()
            }
            // Press feedback only; the tap above performs the action on mouse-up.
            .simultaneousGesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { _ in if !isPressed { isPressed = true } }
                    .onEnded { _ in isPressed = false }
            )
            .onDisappear {
                if isHovered, tooltip != nil {
                    FloatingTooltipWindow.shared.cancelAndHide()
                }
                isPressed = false
            }
            .accessibilityElement()
            .accessibilityLabel(label)
            .accessibilityAddTraits(.isButton)
            .accessibilityAction { action() }
    }

    private func setHovered(_ inside: Bool) {
        guard inside != isHovered else { return }
        isHovered = inside
        if !inside { isPressed = false }
        if let tooltip {
            if inside {
                FloatingTooltipWindow.shared.scheduleShow(text: tooltip)
            } else {
                FloatingTooltipWindow.shared.cancelAndHide()
            }
        }
    }
}

/// `onHover` only while enabled.
private struct OptionalHover: ViewModifier {
    let isEnabled: Bool
    let action: (Bool) -> Void

    func body(content: Content) -> some View {
        if isEnabled {
            content.onHover(perform: action)
        } else {
            content
        }
    }
}

/// Reports the pointer's position over the list (in the list's viewport,
/// top-left origin), or nil when it leaves. While the list scrolls the hover
/// is dropped, and picked up again once scrolling settles, so scrolling costs
/// no hover work at all.
private struct RowHoverTracker: NSViewRepresentable {
    let scrollView: NSScrollView?
    let onMove: (CGPoint?) -> Void

    func makeNSView(context: Context) -> RowHoverTrackingView {
        let view = RowHoverTrackingView()
        view.onMove = onMove
        view.scrollView = scrollView
        return view
    }

    func updateNSView(_ view: RowHoverTrackingView, context: Context) {
        view.onMove = onMove
        view.scrollView = scrollView
    }
}

final class RowHoverTrackingView: NSView {
    var onMove: ((CGPoint?) -> Void)?
    weak var scrollView: NSScrollView? {
        didSet {
            guard scrollView !== oldValue else { return }
            if let observer { NotificationCenter.default.removeObserver(observer) }
            observer = nil
            guard let clip = scrollView?.contentView else { return }
            clip.postsBoundsChangedNotifications = true
            observer = NotificationCenter.default.addObserver(
                forName: NSView.boundsDidChangeNotification, object: clip, queue: .main
            ) { [weak self] _ in
                MainActor.assumeIsolated { self?.scrolled() }
            }
        }
    }
    private var observer: NSObjectProtocol?
    private var trackingArea: NSTrackingArea?
    private var isScrolling = false
    private var settleWork: DispatchWorkItem?

    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    deinit {
        if let observer { NotificationCenter.default.removeObserver(observer) }
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        let area = NSTrackingArea(
            rect: .zero,
            options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect],
            owner: self
        )
        addTrackingArea(area)
        trackingArea = area
    }

    override func mouseEntered(with event: NSEvent) { report(event) }
    override func mouseMoved(with event: NSEvent) { report(event) }
    override func mouseExited(with event: NSEvent) { onMove?(nil) }

    private func report(_ event: NSEvent) {
        guard !isScrolling else { return }
        onMove?(convert(event.locationInWindow, from: nil))
    }

    private func scrolled() {
        if !isScrolling {
            isScrolling = true
            onMove?(nil)
        }
        settleWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.isScrolling = false
            guard let window = self.window else { return }
            let point = self.convert(window.mouseLocationOutsideOfEventStream, from: nil)
            if self.bounds.contains(point) { self.onMove?(point) }
        }
        settleWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12, execute: work)
    }
}

/// Which history row the pointer is over.
@MainActor
final class RowHoverModel: ObservableObject {
    @Published private(set) var hoveredID: ClipItem.ID?

    func set(_ id: ClipItem.ID?) {
        if hoveredID != id { hoveredID = id }
    }
}

/// Hands a row whether it is hovered. Only this tiny view observes the
/// hover model; the row itself is equatable, so moving the pointer to
/// another row re-renders just the two rows involved.
private struct RowHoverGate<Content: View>: View {
    @ObservedObject var model: RowHoverModel
    let id: ClipItem.ID
    @ViewBuilder let content: (Bool) -> Content

    var body: some View {
        content(model.hoveredID == id)
    }
}

private extension View {
    /// Delayed hover tooltip. Driven by SwiftUI hover rather than an AppKit
    /// tracking view per button, so list rows stay pure SwiftUI while scrolling.
    func floatingTooltip(_ text: String) -> some View {
        modifier(FloatingTooltipModifier(text: text))
    }
}

private struct FloatingTooltipModifier: ViewModifier {
    let text: String
    @State private var isHovering = false

    func body(content: Content) -> some View {
        content
            .onHover { inside in
                isHovering = inside
                if inside {
                    FloatingTooltipWindow.shared.scheduleShow(text: text)
                } else {
                    FloatingTooltipWindow.shared.cancelAndHide()
                }
            }
            .onDisappear {
                if isHovering {
                    FloatingTooltipWindow.shared.cancelAndHide()
                }
            }
    }
}

private final class FloatingTooltipWindow {
    static let shared = FloatingTooltipWindow()
    private var window: NSWindow?
    private let label = NSTextField(labelWithString: "")

    private var pendingShow: DispatchWorkItem?
    private var clickMonitor: Any?

    func scheduleShow(text: String) {
        pendingShow?.cancel()
        hide()
        installClickMonitorIfNeeded()
        let task = DispatchWorkItem { [weak self] in
            guard NSApp.isActive, let keyWindow = NSApp.keyWindow else { return }
            self?.show(text: text, near: NSEvent.mouseLocation, screen: keyWindow.screen)
        }
        pendingShow = task
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.55, execute: task)
    }

    func cancelAndHide() {
        pendingShow?.cancel()
        pendingShow = nil
        hide()
    }

    /// Like system tooltips, any click dismisses a pending or visible tooltip.
    private func installClickMonitorIfNeeded() {
        guard clickMonitor == nil else { return }
        clickMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .keyDown]) { [weak self] event in
            self?.cancelAndHide()
            return event
        }
    }

    private init() {
        label.font = .systemFont(ofSize: 11)
        label.textColor = .white
        label.alignment = .center
        label.lineBreakMode = .byClipping
        label.maximumNumberOfLines = 1
        label.backgroundColor = .clear
    }

    private func show(text: String, near point: NSPoint, screen: NSScreen?) {
        label.stringValue = text
        let textSize = label.intrinsicContentSize
        let size = NSSize(width: textSize.width + 16, height: textSize.height + 10)
        let screenFrame = screen?.visibleFrame ?? NSScreen.main?.visibleFrame ?? .zero
        // Below the pointer like system tooltips; flip above near the bottom edge.
        let below = point.y - 26 - size.height
        let originY = below > screenFrame.minY ? below : point.y + 16
        let originX = min(max(point.x - size.width / 2, screenFrame.minX + 8), screenFrame.maxX - size.width - 8)

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

    final class Coordinator {
        weak var scrollView: NSScrollView?
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        let coordinator = context.coordinator
        DispatchQueue.main.async {
            resolve(from: view, coordinator: coordinator)
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        // Found once and remembered: every update of the main view used to
        // search the window's whole view tree for the list again.
        if let known = context.coordinator.scrollView, known.window != nil, known.window === nsView.window {
            return
        }
        let coordinator = context.coordinator
        DispatchQueue.main.async {
            resolve(from: nsView, coordinator: coordinator)
        }
    }

    private func resolve(from view: NSView, coordinator: Coordinator) {
        let scrollView = resolveScrollView(from: view)
        coordinator.scrollView = scrollView
        configure(scrollView)
        onResolve(scrollView)
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

final class DragHighlightModel: ObservableObject {
    @Published private(set) var range: ClosedRange<Int>?
    @Published private(set) var opacity: Double = 1
    /// Invalidates a pending fade when a new drag starts.
    private var fadeToken = 0

    func show(_ range: ClosedRange<Int>) {
        fadeToken += 1
        var transaction = Transaction()
        transaction.disablesAnimations = true
        withTransaction(transaction) { opacity = 1 }
        // Rows joining or leaving the range fade in and out.
        withAnimation(.easeOut(duration: 0.16)) { self.range = range }
    }

    func beginFade() -> Int { fadeToken }

    func fadeOut(_ token: Int) {
        guard token == fadeToken else { return }
        opacity = 0
    }

    func clear(_ token: Int) {
        guard token == fadeToken else { return }
        range = nil
        opacity = 1
    }
}

/// The rows being drag-selected, as light accent-coloured cards over the
/// list (like Finder's selection): one shape, however many rows.
private struct DragRangeHighlight: View {
    @ObservedObject var model: DragHighlightModel
    let rowHeight: CGFloat
    let inset: CGFloat

    var body: some View {
        // One shape per row, so each row's highlight fades in softly as the
        // drag reaches it (one shape for the whole range jumped a row at a
        // time). Plain shapes, not glass: cheap to animate while dragging.
        ZStack(alignment: .top) {
            if let range = model.range {
                ForEach(Array(range), id: \.self) { index in
                    let shape = RoundedRectangle(cornerRadius: GlassMetrics.cardRadius, style: .continuous)
                    shape
                        .fill(Color.accentColor.opacity(0.16))
                        .overlay(shape.strokeBorder(Color.accentColor.opacity(0.6), lineWidth: 1.2))
                        .frame(height: rowHeight - inset * 2)
                        .offset(y: CGFloat(index) * rowHeight + inset)
                        .transition(AccessibilityLook.current.reduceMotion ? .opacity : .opacity.combined(with: .scale(scale: 0.97)))
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .top)
        .opacity(model.opacity)
        .allowsHitTesting(false)
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

            // Only the list's own window: a drag in the preview window above
            // it used to select the records underneath.
            guard event.window === NSApp.keyWindow, event.window === view?.window else {
                cancelDrag()
                return event
            }

            guard let point = localTopLeftPoint(from: event) else {
                cancelDrag()
                return event
            }

            switch event.type {
            case .leftMouseDown:
                // A three-finger drag on blank list space moves the window,
                // like dragging its title bar; on a record it selects as
                // usual, and on the toolbar's controls it is left to them.
                if TrackpadFingers.count >= 3, isPointInsideViewport(point), !isOnRecord(event), let window = event.window {
                    cancelDrag()
                    window.dragWindow(with: event)
                    return nil
                }
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
            // Found once and remembered: walking the whole window's view
            // tree on every mouse event made drag-selecting stutter.
            if let cachedScrollView, cachedScrollView.window != nil, cachedScrollView.window === view?.window {
                return cachedScrollView
            }

            guard let contentView = view?.window?.contentView else {
                return nil
            }

            cachedScrollView = firstScrollView(in: contentView)
            return cachedScrollView
        }

        private weak var cachedScrollView: NSScrollView?

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

        /// Whether the pointer is over a record's card.
        private func isOnRecord(_ event: NSEvent) -> Bool {
            guard let mouseY = documentY(from: event),
                  let point = localTopLeftPoint(from: event),
                  isPointInsideViewport(point),
                  let viewport = viewportView() else { return false }
            let index = Int(floor((mouseY - contentTopInset) / rowHeight))
            return itemIDs.indices.contains(index)
                && point.x >= contentHorizontalInset
                && point.x <= viewport.bounds.width - contentHorizontalInset
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
            // Common modes include event tracking.
            RunLoop.main.add(timer, forMode: .common)
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

enum ToolbarSpace {
    static let name = "ClipShelfToolbar"
}
