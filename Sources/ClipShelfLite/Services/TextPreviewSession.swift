import AppKit
import Foundation

final class TextPreviewSession: NSObject, NSSearchFieldDelegate {
    let rootView: NSView
    let firstResponder: NSResponder

    private let scrollView: NSScrollView
    private let textView: NSTextView
    private let rootContainer: TextPreviewRootView
    private let lineNumberRuler: TextLineNumberRulerView
    private let lineNumberButton: NSButton
    private let wrapButton: NSButton
    private let encodingLabel: NSTextField
    private let findField: NSSearchField
    private let matchLabel: NSTextField
    private var loadTask: Task<Void, Never>?
    private var matches = [NSRange]()
    private var currentMatchIndex = -1

    init(frame: NSRect, text: String, encodingName: String) {
        scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.drawsBackground = false

        textView = NSTextView()
        textView.isEditable = false
        textView.isSelectable = true
        textView.isVerticallyResizable = true
        textView.font = .monospacedSystemFont(ofSize: 14, weight: .regular)
        textView.textContainerInset = NSSize(width: 18, height: 18)
        textView.string = text
        scrollView.documentView = textView

        lineNumberButton = NSButton(checkboxWithTitle: "行号", target: nil, action: nil)
        lineNumberButton.state = .on
        wrapButton = NSButton(checkboxWithTitle: "自动换行", target: nil, action: nil)
        wrapButton.state = .on
        encodingLabel = NSTextField(labelWithString: encodingName)
        encodingLabel.textColor = .secondaryLabelColor
        encodingLabel.lineBreakMode = .byTruncatingTail

        let toolbar = NSVisualEffectView()
        toolbar.material = .headerView
        toolbar.blendingMode = .withinWindow
        toolbar.state = .active
        let toolbarStack = NSStackView(views: [lineNumberButton, wrapButton, NSView(), encodingLabel])
        toolbarStack.orientation = .horizontal
        toolbarStack.alignment = .centerY
        toolbarStack.spacing = 14
        toolbarStack.edgeInsets = NSEdgeInsets(top: 7, left: 12, bottom: 7, right: 12)
        toolbarStack.frame = toolbar.bounds
        toolbarStack.autoresizingMask = [.width, .height]
        toolbar.addSubview(toolbarStack)

        findField = NSSearchField()
        findField.placeholderString = "查找"
        findField.sendsSearchStringImmediately = true
        findField.sendsWholeSearchString = true
        let previousButton = NSButton(image: NSImage(systemSymbolName: "chevron.up", accessibilityDescription: "上一个") ?? NSImage(), target: nil, action: nil)
        let nextButton = NSButton(image: NSImage(systemSymbolName: "chevron.down", accessibilityDescription: "下一个") ?? NSImage(), target: nil, action: nil)
        let closeButton = NSButton(image: NSImage(systemSymbolName: "xmark", accessibilityDescription: "关闭查找") ?? NSImage(), target: nil, action: nil)
        [previousButton, nextButton, closeButton].forEach { $0.isBordered = false }
        matchLabel = NSTextField(labelWithString: "")
        matchLabel.textColor = .secondaryLabelColor
        matchLabel.alignment = .right
        matchLabel.setContentHuggingPriority(.required, for: .horizontal)

        let findBar = NSVisualEffectView()
        findBar.material = .sidebar
        findBar.blendingMode = .withinWindow
        findBar.state = .active
        let findStack = NSStackView(views: [findField, matchLabel, previousButton, nextButton, closeButton])
        findStack.orientation = .horizontal
        findStack.alignment = .centerY
        findStack.spacing = 8
        findStack.edgeInsets = NSEdgeInsets(top: 5, left: 12, bottom: 5, right: 12)
        findStack.frame = findBar.bounds
        findStack.autoresizingMask = [.width, .height]
        findBar.addSubview(findStack)

        rootContainer = TextPreviewRootView(frame: frame, toolbar: toolbar, findBar: findBar, body: scrollView)
        rootView = rootContainer
        firstResponder = textView

        lineNumberRuler = TextLineNumberRulerView(scrollView: scrollView, textView: textView)

        super.init()

        lineNumberButton.target = self
        lineNumberButton.action = #selector(toggleLineNumbers)
        wrapButton.target = self
        wrapButton.action = #selector(toggleWrap)
        previousButton.target = self
        previousButton.action = #selector(findPrevious)
        nextButton.target = self
        nextButton.action = #selector(findNext)
        closeButton.target = self
        closeButton.action = #selector(hideFindBar)
        findField.delegate = self

        scrollView.hasVerticalRuler = true
        scrollView.verticalRulerView = lineNumberRuler
        scrollView.rulersVisible = true
        applyWrap(true)
        lineNumberRuler.updateLineStarts(text: text)
    }

    deinit {
        loadTask?.cancel()
    }

    var isFindFieldEditing: Bool {
        guard let window = findField.window else { return false }
        return findField.currentEditor() === window.firstResponder
    }

    func setText(_ text: String, encodingName: String) {
        textView.string = text
        encodingLabel.stringValue = encodingName
        lineNumberRuler.updateLineStarts(text: text)
        updateMatches(selectFirst: true)
        applyWrap(wrapButton.state == .on)
    }

    func loadFile(_ url: URL) {
        loadTask?.cancel()
        encodingLabel.stringValue = "正在分段读取并检测编码…"
        loadTask = Task { @MainActor [weak self] in
            do {
                let result = try await TextFilePreviewLoader.load(url)
                guard !Task.isCancelled else { return }
                let sizeText = ByteCountFormatter.string(fromByteCount: Int64(result.loadedBytes), countStyle: .file)
                let suffix = result.isTruncated ? " · 大文件预览前 \(sizeText)" : " · \(sizeText)"
                self?.setText(result.decoded.text, encodingName: result.decoded.encodingName + suffix)
            } catch is CancellationError {
                return
            } catch {
                self?.setText("无法读取文本文件。\n\n\(error.localizedDescription)", encodingName: "读取失败")
            }
        }
    }

    func cancel() {
        loadTask?.cancel()
        loadTask = nil
    }

    func handleKeyDown(_ event: NSEvent) -> Bool {
        let command = event.modifierFlags.contains(.command)
        if command && event.keyCode == 3 {
            showFindBar()
            return true
        }
        if event.keyCode == 99 {
            moveToMatch(backward: event.modifierFlags.contains(.shift))
            return true
        }
        if event.keyCode == 53, rootContainer.isFindBarVisible {
            hideFindBar()
            return true
        }
        return false
    }

    func showFindBar() {
        rootContainer.setFindBarVisible(true)
        findField.window?.makeFirstResponder(findField)
        updateMatches(selectFirst: currentMatchIndex < 0)
    }

    @objc private func hideFindBar() {
        rootContainer.setFindBarVisible(false)
        textView.window?.makeFirstResponder(textView)
    }

    @objc private func toggleLineNumbers() {
        scrollView.rulersVisible = lineNumberButton.state == .on
    }

    @objc private func toggleWrap() {
        applyWrap(wrapButton.state == .on)
    }

    @objc private func findNext() {
        moveToMatch(backward: false)
    }

    @objc private func findPrevious() {
        moveToMatch(backward: true)
    }

    func controlTextDidChange(_ obj: Notification) {
        updateMatches(selectFirst: true)
    }

    private func applyWrap(_ enabled: Bool) {
        guard let textContainer = textView.textContainer,
              let layoutManager = textView.layoutManager else { return }
        textView.isHorizontallyResizable = !enabled
        textContainer.widthTracksTextView = enabled
        scrollView.hasHorizontalScroller = !enabled

        if enabled {
            textView.autoresizingMask = [.width]
            textContainer.containerSize = NSSize(
                width: max(1, scrollView.contentSize.width),
                height: .greatestFiniteMagnitude
            )
            textView.frame.size.width = max(1, scrollView.contentSize.width)
        } else {
            textView.autoresizingMask = []
            textContainer.containerSize = NSSize(
                width: CGFloat.greatestFiniteMagnitude,
                height: CGFloat.greatestFiniteMagnitude
            )
            layoutManager.ensureLayout(for: textContainer)
            let used = layoutManager.usedRect(for: textContainer)
            textView.frame.size.width = max(scrollView.contentSize.width, used.width + 36)
        }
        layoutManager.ensureLayout(for: textContainer)
        let used = layoutManager.usedRect(for: textContainer)
        textView.frame.size.height = max(scrollView.contentSize.height, used.height + 36)
        lineNumberRuler.needsDisplay = true
    }

    private func updateMatches(selectFirst: Bool) {
        let query = findField.stringValue
        matches.removeAll()
        currentMatchIndex = -1
        guard !query.isEmpty else {
            matchLabel.stringValue = ""
            return
        }

        let source = textView.string as NSString
        var searchRange = NSRange(location: 0, length: source.length)
        while searchRange.length > 0 {
            let match = source.range(of: query, options: [.caseInsensitive], range: searchRange)
            guard match.location != NSNotFound else { break }
            matches.append(match)
            let nextLocation = NSMaxRange(match)
            searchRange = NSRange(location: nextLocation, length: source.length - nextLocation)
        }

        if selectFirst, !matches.isEmpty {
            currentMatchIndex = 0
            revealCurrentMatch()
        } else {
            updateMatchLabel()
        }
    }

    private func moveToMatch(backward: Bool) {
        if matches.isEmpty {
            updateMatches(selectFirst: true)
            return
        }
        currentMatchIndex = backward
            ? (currentMatchIndex <= 0 ? matches.count - 1 : currentMatchIndex - 1)
            : (currentMatchIndex + 1) % matches.count
        revealCurrentMatch()
    }

    private func revealCurrentMatch() {
        guard matches.indices.contains(currentMatchIndex) else {
            updateMatchLabel()
            return
        }
        let range = matches[currentMatchIndex]
        textView.setSelectedRange(range)
        textView.scrollRangeToVisible(range)
        updateMatchLabel()
    }

    private func updateMatchLabel() {
        guard !matches.isEmpty, currentMatchIndex >= 0 else {
            matchLabel.stringValue = findField.stringValue.isEmpty ? "" : "0 项"
            return
        }
        matchLabel.stringValue = "\(currentMatchIndex + 1) / \(matches.count)"
    }
}

private final class TextPreviewRootView: NSView {
    private let toolbar: NSView
    private let findBar: NSView
    private let bodyView: NSView
    private(set) var isFindBarVisible = false
    private let toolbarHeight: CGFloat = 42
    private let findBarHeight: CGFloat = 38

    init(frame: NSRect, toolbar: NSView, findBar: NSView, body: NSView) {
        self.toolbar = toolbar
        self.findBar = findBar
        bodyView = body
        super.init(frame: frame)
        autoresizingMask = [.width, .height]
        addSubview(body)
        addSubview(findBar)
        addSubview(toolbar)
        findBar.isHidden = true
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func setFindBarVisible(_ visible: Bool) {
        guard isFindBarVisible != visible else { return }
        isFindBarVisible = visible
        findBar.isHidden = !visible
        needsLayout = true
        layoutSubtreeIfNeeded()
    }

    override func layout() {
        super.layout()
        toolbar.frame = NSRect(x: 0, y: bounds.height - toolbarHeight, width: bounds.width, height: toolbarHeight)
        let findHeight = isFindBarVisible ? findBarHeight : 0
        findBar.frame = NSRect(
            x: 0,
            y: bounds.height - toolbarHeight - findHeight,
            width: bounds.width,
            height: findHeight
        )
        bodyView.frame = NSRect(
            x: 0,
            y: 0,
            width: bounds.width,
            height: max(1, bounds.height - toolbarHeight - findHeight)
        )
    }
}

private final class TextLineNumberRulerView: NSRulerView {
    private weak var textView: NSTextView?
    private var lineStarts = [0]

    init(scrollView: NSScrollView, textView: NSTextView) {
        self.textView = textView
        super.init(scrollView: scrollView, orientation: .verticalRuler)
        clientView = textView
        ruleThickness = 46
    }

    required init(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    func updateLineStarts(text: String) {
        lineStarts = [0]
        for (index, character) in text.utf16.enumerated() where character == 10 {
            lineStarts.append(index + 1)
        }
        needsDisplay = true
    }

    override func drawHashMarksAndLabels(in rect: NSRect) {
        guard let textView,
              let layoutManager = textView.layoutManager,
              let textContainer = textView.textContainer else { return }

        NSColor.windowBackgroundColor.setFill()
        bounds.fill()
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .regular),
            .foregroundColor: NSColor.secondaryLabelColor
        ]
        let visible = scrollView?.contentView.bounds ?? .zero
        let characterCount = (textView.string as NSString).length

        for (index, start) in lineStarts.enumerated() where start <= characterCount {
            let characterIndex = min(start, max(0, characterCount - 1))
            let glyphIndex = characterCount == 0 ? 0 : layoutManager.glyphIndexForCharacter(at: characterIndex)
            guard layoutManager.numberOfGlyphs == 0 || glyphIndex < layoutManager.numberOfGlyphs else { continue }
            let fragment = layoutManager.numberOfGlyphs == 0
                ? NSRect(x: 0, y: 0, width: 1, height: textView.font?.pointSize ?? 14)
                : layoutManager.lineFragmentRect(forGlyphAt: glyphIndex, effectiveRange: nil)
            let textY = fragment.minY + textView.textContainerInset.height
            guard textY + fragment.height >= visible.minY, textY <= visible.maxY else { continue }
            let point = convert(NSPoint(x: 0, y: textY), from: textView)
            let label = "\(index + 1)" as NSString
            let size = label.size(withAttributes: attributes)
            label.draw(
                at: NSPoint(x: ruleThickness - size.width - 8, y: point.y + 1),
                withAttributes: attributes
            )
        }

        NSColor.separatorColor.setFill()
        NSRect(x: bounds.maxX - 1, y: bounds.minY, width: 1, height: bounds.height).fill()
        _ = textContainer
    }
}
