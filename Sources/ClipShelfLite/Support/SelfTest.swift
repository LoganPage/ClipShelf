import AppKit
import Foundation

struct SelfTestResult: Codable, Equatable {
    let name: String
    let passed: Bool
    let detail: String
}

enum ClipShelfSelfTest {
    static func run(fileManager: FileManager = .default) -> [SelfTestResult] {
        let temporaryRoot = fileManager.temporaryDirectory
            .appendingPathComponent("ClipShelf-SelfTest-\(UUID().uuidString)", isDirectory: true)
        let fixedSuiteName = "ClipShelf.SelfTest.Active"
        let suiteName = fixedSuiteName
        let preferencesURL = fileManager.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Preferences", isDirectory: true)
        let suitePlistURL = preferencesURL.appendingPathComponent("\(suiteName).plist")
        let allowedSelfTestDefaultsFiles: Set<String> = ["\(fixedSuiteName).plist"]
        let selfTestDefaultsFiles = {
            let contents = (try? fileManager.contentsOfDirectory(
                at: preferencesURL,
                includingPropertiesForKeys: nil
            )) ?? []
            return Set(contents.compactMap { url in
                let name = url.lastPathComponent
                return name.hasPrefix("ClipShelf.SelfTest.") && url.pathExtension == "plist" ? name : nil
            })
        }
        let clearSelfTestDefaults = {
            let defaults = UserDefaults(suiteName: suiteName)
            defaults?.removePersistentDomain(forName: suiteName)
            defaults?.synchronize()
            try? fileManager.removeItem(at: suitePlistURL)
        }
        let environment = [
            "CLIPSHELF_DATA_DIR": temporaryRoot.path,
            "CLIPSHELF_DEFAULTS_SUITE": suiteName
        ]

        clearSelfTestDefaults()
        defer {
            try? fileManager.removeItem(at: temporaryRoot)
            clearSelfTestDefaults()
        }

        var results = [SelfTestResult]()
        var concealedChangeCount = 10
        let concealedShouldCapture = ClipboardHistoryPolicy.shouldCapture(
            historyEnabled: true,
            observedChangeCount: 11,
            previousChangeCount: &concealedChangeCount,
            typeNames: [
                NSPasteboard.PasteboardType.string.rawValue,
                ClipboardHistoryPolicy.concealedType.rawValue
            ]
        )
        results.append(check(
            name: "concealed content is excluded",
            condition: !concealedShouldCapture,
            success: "ConcealedType is rejected before history capture",
            failure: "ConcealedType was accepted"
        ))
        results.append(check(
            name: "excluded content advances change count",
            condition: concealedChangeCount == 11,
            success: "Excluded clipboard changes are acknowledged exactly once",
            failure: "Excluded clipboard changes would be processed repeatedly"
        ))

        var transientChangeCount = 20
        let transientShouldCapture = ClipboardHistoryPolicy.shouldCapture(
            historyEnabled: true,
            observedChangeCount: 21,
            previousChangeCount: &transientChangeCount,
            typeNames: [ClipboardHistoryPolicy.transientType.rawValue]
        )
        results.append(check(
            name: "transient content is excluded",
            condition: !transientShouldCapture,
            success: "TransientType is rejected before history capture",
            failure: "TransientType was accepted"
        ))

        var ordinaryChangeCount = 30
        let ordinaryShouldCapture = ClipboardHistoryPolicy.shouldCapture(
            historyEnabled: true,
            observedChangeCount: 31,
            previousChangeCount: &ordinaryChangeCount,
            typeNames: [NSPasteboard.PasteboardType.string.rawValue]
        )
        results.append(check(
            name: "ordinary content is included",
            condition: ordinaryShouldCapture && ordinaryChangeCount == 31,
            success: "Unmarked text remains eligible for history capture",
            failure: "Unmarked text was rejected"
        ))
        results.append(check(
            name: "unknown type information is included",
            condition: !ClipboardHistoryPolicy.shouldExclude(typeNames: nil),
            success: "Unavailable type information fails open",
            failure: "Unavailable type information caused content loss"
        ))

        var pausedChangeCount = 40
        let pausedShouldCapture = ClipboardHistoryPolicy.shouldCapture(
            historyEnabled: false,
            observedChangeCount: 41,
            previousChangeCount: &pausedChangeCount,
            typeNames: [NSPasteboard.PasteboardType.string.rawValue]
        )
        results.append(check(
            name: "paused clipboard history rejects capture",
            condition: !pausedShouldCapture && pausedChangeCount == 40,
            success: "Disabled clipboard history does not capture pasteboard changes",
            failure: "Clipboard content was accepted while history recording was paused"
        ))
        results.append(check(
            name: "status menu recording title follows history state",
            condition: StatusMenuText.recordingToggleTitle(isEnabled: true) == "暂停记录"
                && StatusMenuText.recordingToggleTitle(isEnabled: false) == "继续记录",
            success: "The status menu title maps directly from the shared history-enabled state",
            failure: "The status menu title does not match the recording state"
        ))

        let historyURL = AppEnvironment.dataDirectory(environment: environment, fileManager: fileManager)
            .appendingPathComponent("history.json")
        do {
            try fileManager.createDirectory(at: temporaryRoot, withIntermediateDirectories: true)
            let fixture = [ClipItem(kind: .text, title: "self-test", text: "self-test")]
            let data = try JSONEncoder().encode(fixture)
            try data.write(to: historyURL, options: .atomic)
            let decoded = try JSONDecoder().decode([ClipItem].self, from: Data(contentsOf: historyURL))
            results.append(check(
                name: "data directory isolation",
                condition: decoded == fixture && historyURL.deletingLastPathComponent() == temporaryRoot,
                success: "history.json was written only inside CLIPSHELF_DATA_DIR",
                failure: "history.json did not use the isolated data directory"
            ))
        } catch {
            results.append(SelfTestResult(
                name: "data directory isolation",
                passed: false,
                detail: error.localizedDescription
            ))
        }

        let defaults = AppEnvironment.userDefaults(environment: environment)
        let defaultsKey = "selfTest.\(UUID().uuidString)"
        defaults.set("isolated", forKey: defaultsKey)
        results.append(check(
            name: "defaults suite isolation",
            condition: defaults.string(forKey: defaultsKey) == "isolated"
                && UserDefaults.standard.object(forKey: defaultsKey) == nil,
            success: "settings were written to CLIPSHELF_DEFAULTS_SUITE",
            failure: "settings escaped the isolated defaults suite"
        ))

        results.append(check(
            name: "history limit default",
            condition: HistoryLimitPreferences.load(from: defaults) == 100,
            success: "A missing history.maxItems key defaults to 100",
            failure: "The default history limit is not 100"
        ))

        HistoryLimitPreferences.save(5, to: defaults)
        let reloadedDefaults = UserDefaults(suiteName: suiteName)
        results.append(check(
            name: "history limit persistence",
            condition: reloadedDefaults.map { HistoryLimitPreferences.load(from: $0) } == 5,
            success: "history.maxItems persists as an integer in the isolated suite",
            failure: "The history limit did not persist across a new defaults instance"
        ))

        results.append(check(
            name: "history limit boundaries",
            condition: HistoryLimitPreferences.normalized(1) == 1
                && HistoryLimitPreferences.normalized(10_000) == 10_000
                && HistoryLimitPreferences.normalized(0) == 1
                && HistoryLimitPreferences.normalized(-1) == 1
                && HistoryLimitPreferences.normalized(10_001) == 10_000,
            success: "Limits are clamped to the inclusive 1...10000 range",
            failure: "At least one numeric boundary was normalized incorrectly"
        ))

        results.append(check(
            name: "invalid history limit input",
            condition: HistoryLimitPreferences.normalized(text: "", fallback: 321) == 321
                && HistoryLimitPreferences.normalized(text: "not-a-number", fallback: 321) == 321,
            success: "Empty and non-numeric input restores the last valid value",
            failure: "Invalid text did not restore the last valid value"
        ))

        let pinnedOne = ClipItem(kind: .text, title: "pinned-one", text: "pinned-one", isPinned: true)
        let pinnedTwo = ClipItem(kind: .text, title: "pinned-two", text: "pinned-two", isPinned: true)
        let newestUnpinned = ClipItem(kind: .text, title: "newest", text: "newest")
        let olderUnpinned = ClipItem(kind: .text, title: "older", text: "older")
        let fixtureFileURL = temporaryRoot.appendingPathComponent("original-file.txt")
        try? Data("original".utf8).write(to: fixtureFileURL)
        let oldestFile = ClipItem(
            kind: .file,
            title: fixtureFileURL.lastPathComponent,
            filePaths: [fixtureFileURL.path]
        )
        var historyFixture = [pinnedOne, pinnedTwo, newestUnpinned, olderUnpinned, oldestFile]
        let didTrim = HistoryTrimmer.trim(&historyFixture, maxItems: 3)
        let remainingIDs = Set(historyFixture.map(\.id))
        results.append(check(
            name: "history limit trims immediately with pinned priority",
            condition: didTrim
                && historyFixture.count == 3
                && remainingIDs.contains(pinnedOne.id)
                && remainingIDs.contains(pinnedTwo.id)
                && remainingIDs.contains(newestUnpinned.id)
                && !remainingIDs.contains(olderUnpinned.id)
                && !remainingIDs.contains(oldestFile.id),
            success: "Lowering the limit removes the oldest unpinned records first",
            failure: "Trimming did not preserve pinned and newest records"
        ))
        results.append(check(
            name: "history trimming preserves original files",
            condition: fileManager.fileExists(atPath: fixtureFileURL.path),
            success: "Removing a file record did not delete its original file",
            failure: "Trimming deleted an original file"
        ))

        let endpointDisabledEnvironment = [
            "CLIPSHELF_DATA_DIR": temporaryRoot.path
        ]
        let endpointIsDisabled: Bool
        do {
            endpointIsDisabled = try AppEnvironment.controlSocketURL(
                environment: endpointDisabledEnvironment,
                fileManager: fileManager
            ) == nil
        } catch {
            endpointIsDisabled = false
        }
        results.append(check(
            name: "runtime control endpoint defaults to disabled",
            condition: endpointIsDisabled,
            success: "No control socket is configured without CLIPSHELF_CONTROL_SOCKET",
            failure: "The control endpoint was enabled by default"
        ))

        let missingSocketURL = temporaryRoot.appendingPathComponent("missing-control.sock")
        let missingSocketEnvironment = [
            "CLIPSHELF_DATA_DIR": temporaryRoot.path,
            "CLIPSHELF_CONTROL_SOCKET": missingSocketURL.path,
            "CLIPSHELF_DEFAULTS_SUITE": suiteName,
            "CLIPSHELF_PASTEBOARD_NAME": "ClipShelf.SelfTest.MissingSocket"
        ]
        let failedControlCall = RuntimeControlCommand.execute(
            arguments: ["ClipShelf", "--ctl", "ping"],
            environment: missingSocketEnvironment
        )
        results.append(check(
            name: "runtime control client reports connection failure",
            condition: failedControlCall.exitCode != 0
                && !failedControlCall.response.ok
                && failedControlCall.response.error?.isEmpty == false,
            success: "An unavailable socket returns a nonzero code and readable JSON error",
            failure: "The control client treated a missing endpoint as success"
        ))

        let pasteboardName = "ClipShelf.SelfTest.Pasteboard.\(UUID().uuidString)"
        let otherPasteboardName = "ClipShelf.SelfTest.OtherPasteboard.\(UUID().uuidString)"
        let namedPasteboard = AppEnvironment.pasteboard(environment: [
            "CLIPSHELF_PASTEBOARD_NAME": pasteboardName
        ])
        let otherPasteboard = NSPasteboard(name: NSPasteboard.Name(otherPasteboardName))
        namedPasteboard.clearContents()
        otherPasteboard.clearContents()
        let generalChangeCount = NSPasteboard.general.changeCount
        namedPasteboard.setString("isolated", forType: .string)
        let namedPasteboardIsIsolated = namedPasteboard.name.rawValue == pasteboardName
            && namedPasteboard.string(forType: .string) == "isolated"
            && otherPasteboard.string(forType: .string) == nil
            && NSPasteboard.general.changeCount == generalChangeCount
            && AppEnvironment.pasteboard(environment: [:]).name == NSPasteboard.general.name
        namedPasteboard.clearContents()
        otherPasteboard.clearContents()
        namedPasteboard.releaseGlobally()
        otherPasteboard.releaseGlobally()
        results.append(check(
            name: "named pasteboard is isolated",
            condition: namedPasteboardIsIsolated,
            success: "CLIPSHELF_PASTEBOARD_NAME does not write to other or general pasteboards",
            failure: "Named pasteboard content escaped its isolated pasteboard"
        ))

        let undoDate = Date(timeIntervalSince1970: 1_700_000_000)
        let undoPinned = ClipItem(
            kind: .text,
            title: "undo-pinned",
            text: "undo-pinned",
            createdAt: undoDate,
            isPinned: true
        )
        let undoMiddle = ClipItem(
            kind: .text,
            title: "undo-middle",
            text: "undo-middle",
            createdAt: undoDate.addingTimeInterval(-1)
        )
        let undoLast = ClipItem(
            kind: .text,
            title: "undo-last",
            text: "undo-last",
            createdAt: undoDate.addingTimeInterval(-2)
        )
        let undoOriginalItems = [undoPinned, undoMiddle, undoLast]
        var undoItems = [undoMiddle]
        var undoStack = HistoryDeletionUndoStack()
        undoStack.record([
            HistoryDeletionEntry(item: undoPinned, originalIndex: 0),
            HistoryDeletionEntry(item: undoLast, originalIndex: 2)
        ])
        let undoRestored = undoStack.undo(into: &undoItems, maxItems: 100)
        results.append(check(
            name: "undo deletion restores order and metadata",
            condition: undoItems == undoOriginalItems
                && undoRestored.map(\.id) == [undoPinned.id, undoLast.id]
                && undoItems.first?.isPinned == true
                && undoItems.first?.createdAt == undoDate,
            success: "Undo restores original IDs, order, timestamps, and pinned state",
            failure: "Undo changed the deleted records or their original order"
        ))

        var cappedUndoStack = HistoryDeletionUndoStack()
        for index in 0..<11 {
            let item = ClipItem(kind: .text, title: "batch-\(index)", text: "batch-\(index)")
            cappedUndoStack.record([HistoryDeletionEntry(item: item, originalIndex: 0)])
        }
        var cappedUndoItems = [ClipItem]()
        var undoneBatchTitles = [String]()
        while cappedUndoStack.canUndo {
            undoneBatchTitles.append(contentsOf: cappedUndoStack.undo(
                into: &cappedUndoItems,
                maxItems: 100
            ).map(\.title))
        }
        results.append(check(
            name: "undo deletion keeps the latest ten batches",
            condition: undoneBatchTitles.count == 10
                && undoneBatchTitles.first == "batch-10"
                && !undoneBatchTitles.contains("batch-0"),
            success: "The in-memory undo stack drops batches older than the latest ten",
            failure: "The undo stack did not enforce its ten-batch capacity"
        ))

        let newerOne = ClipItem(kind: .text, title: "newer-one", text: "newer-one")
        let newerTwo = ClipItem(kind: .text, title: "newer-two", text: "newer-two")
        var capacityItems = [newerOne, newerTwo]
        var capacityUndoStack = HistoryDeletionUndoStack()
        capacityUndoStack.record(undoOriginalItems.enumerated().map { index, item in
            HistoryDeletionEntry(item: item, originalIndex: index)
        })
        let capacityRestored = capacityUndoStack.undo(into: &capacityItems, maxItems: 3)
        results.append(check(
            name: "undo deletion respects current history capacity",
            condition: capacityItems.count == 3
                && capacityItems.contains(newerOne)
                && capacityItems.contains(newerTwo)
                && capacityRestored.count == 1,
            success: "Undo fills only free slots and never evicts newer records",
            failure: "Undo displaced records added after the deletion"
        ))

        var clearUndoItems = [ClipItem]()
        var clearUndoStack = HistoryDeletionUndoStack()
        clearUndoStack.record(undoOriginalItems.enumerated().map { index, item in
            HistoryDeletionEntry(item: item, originalIndex: index)
        })
        _ = clearUndoStack.undo(into: &clearUndoItems, maxItems: 100)
        results.append(check(
            name: "clear history is undoable",
            condition: clearUndoItems == undoOriginalItems
                && !HistoryDeletionUndoStack().canUndo,
            success: "A clear-history batch restores in full and a new session starts empty",
            failure: "Clear-history undo or fresh-session state is incorrect"
        ))

        let fileBatchDate = Date(timeIntervalSince1970: 1_710_000_000)
        let filePaths = [
            "/tmp/clipshelf-batch/one.txt",
            "/tmp/clipshelf-batch/two.pdf",
            "/tmp/clipshelf-batch/three.png"
        ]
        let splitFileItems = FileHistoryBatchPlanner.newItems(
            for: filePaths,
            existingItems: [],
            createdAt: fileBatchDate
        )
        results.append(check(
            name: "multi-file import splits into individual records",
            condition: splitFileItems.count == 3
                && splitFileItems.allSatisfy { $0.kind == .file && $0.filePaths.count == 1 }
                && splitFileItems.map(\.title) == ["one.txt", "two.pdf", "three.png"]
                && splitFileItems.map { $0.filePaths[0] } == filePaths,
            success: "Each copied file becomes one independently addressable history record",
            failure: "A multi-file batch was not split at single-file granularity"
        ))

        let duplicatePlan = FileHistoryBatchPlanner.newItems(
            for: filePaths + [filePaths[0]],
            existingItems: splitFileItems,
            createdAt: fileBatchDate.addingTimeInterval(10)
        )
        results.append(check(
            name: "multi-file import deduplicates by path",
            condition: duplicatePlan.isEmpty,
            success: "Repeated paths do not create additional file records",
            failure: "An existing file path was imported again"
        ))

        let preservedFile = ClipItem(
            id: UUID(),
            kind: .file,
            title: "one.txt",
            filePaths: [filePaths[0]],
            createdAt: fileBatchDate.addingTimeInterval(-100),
            isPinned: true
        )
        let preservedSnapshot = preservedFile
        let mixedFilePlan = FileHistoryBatchPlanner.newItems(
            for: [filePaths[0], "/tmp/clipshelf-batch/four.txt"],
            existingItems: [preservedFile],
            createdAt: fileBatchDate
        )
        results.append(check(
            name: "file path dedup preserves existing metadata",
            condition: preservedFile == preservedSnapshot
                && mixedFilePlan.count == 1
                && mixedFilePlan[0].title == "four.txt"
                && preservedFile.isPinned
                && preservedFile.createdAt == preservedSnapshot.createdAt,
            success: "Path dedup leaves the existing ID, timestamp, and pinned state untouched",
            failure: "Deduplication replaced or modified an existing file record"
        ))

        let legacyCombinedItem = ClipItem(
            kind: .file,
            title: "2 个文件",
            filePaths: [filePaths[0], filePaths[1]],
            createdAt: fileBatchDate
        )
        let legacyRoundTrip = try? JSONDecoder().decode(
            [ClipItem].self,
            from: JSONEncoder().encode([legacyCombinedItem])
        )
        let legacyAdditionalItems = FileHistoryBatchPlanner.newItems(
            for: [filePaths[0], filePaths[1]],
            existingItems: legacyRoundTrip ?? [],
            createdAt: fileBatchDate
        )
        results.append(check(
            name: "legacy combined file records remain compatible",
            condition: legacyRoundTrip?.count == 1
                && legacyRoundTrip?.first?.filePaths.count == 2
                && legacyAdditionalItems.isEmpty,
            success: "Old multi-path records decode unchanged and participate in path dedup",
            failure: "A legacy file record was split or could not be decoded"
        ))

        var limitedFileItems = [
            ClipItem(
                kind: .text,
                title: "pinned-history",
                text: "pinned-history",
                createdAt: fileBatchDate.addingTimeInterval(-200),
                isPinned: true
            )
        ] + splitFileItems
        _ = HistoryTrimmer.trim(&limitedFileItems, maxItems: 2)
        results.append(check(
            name: "multi-file import obeys the history limit",
            condition: limitedFileItems.count == 2
                && limitedFileItems.contains { $0.title == "pinned-history" && $0.isPinned },
            success: "Batch file records use the existing pinned-first history trimming rule",
            failure: "Batch import exceeded the limit or removed a pinned record first"
        ))

        let filterItems = [
            ClipItem(kind: .text, title: "Meeting notes", text: "project nebula"),
            ClipItem(kind: .file, title: "nebula.pdf", filePaths: ["/tmp/nebula.pdf"]),
            ClipItem(kind: .image, title: "diagram.png", sourcePath: "/tmp/diagram.png")
        ]
        let expectedKinds: [(ClipKindFilter, [ClipItem.Kind])] = [
            (.all, [.text, .file, .image]),
            (.text, [.text]),
            (.file, [.file]),
            (.image, [.image])
        ]
        results.append(check(
            name: "record type filter covers every option",
            condition: expectedKinds.allSatisfy { filter, kinds in
                ClipHistoryFilter.items(filterItems, kind: filter, query: "").map(\.kind) == kinds
            },
            success: "All, text, file, and image filters return only their intended records",
            failure: "At least one record type filter returned the wrong kinds"
        ))
        results.append(check(
            name: "record type filter composes with search",
            condition: ClipHistoryFilter.items(filterItems, kind: .file, query: "nebula").map(\.kind) == [.file]
                && ClipHistoryFilter.items(filterItems, kind: .text, query: "nebula").map(\.kind) == [.text]
                && ClipHistoryFilter.items(filterItems, kind: .image, query: "nebula").isEmpty,
            success: "Type filtering and text search are applied through one combined funnel",
            failure: "Type filtering replaced search or search bypassed the selected type"
        ))
        results.append(check(
            name: "record type filter reports an empty result",
            condition: !filterItems.isEmpty
                && ClipHistoryFilter.items(filterItems, kind: .image, query: "missing").isEmpty,
            success: "A nonempty history can produce a distinct empty filtered result",
            failure: "The empty filtered-result state could not be distinguished"
        ))
        results.append(check(
            name: "selection count label follows the selected set size",
            condition: SelectionCountLabel.text(for: 0) == nil
                && SelectionCountLabel.text(for: -1) == nil
                && SelectionCountLabel.text(for: 1) == "已选 1 条"
                && SelectionCountLabel.text(for: 3) == "已选 3 条"
                && SelectionCountLabel.text(for: 300) == "已选 300 条",
            success: "The label is hidden at zero and formats one-, two-, or three-digit counts directly",
            failure: "The selection count label visibility or text is incorrect"
        ))

        let fileTypeCases: [(String?, Bool, FileTypeIconCategory)] = [
            ("/tmp/report.pdf", false, .pdf),
            ("/tmp/table.XLSX", false, .spreadsheet),
            ("/tmp/letter.docx", false, .word),
            ("/tmp/deck.pptx", false, .presentation),
            ("/tmp/archive.zip", false, .archive),
            ("/tmp/folder", true, .folder),
            ("/tmp/unknown.bin", false, .generic),
            ("/tmp/no-extension", false, .generic),
            (nil, false, .generic)
        ]
        results.append(check(
            name: "file type icon maps supported extensions and fallbacks",
            condition: fileTypeCases.allSatisfy { path, isDirectory, expected in
                FileTypeIcon.category(forPath: path, isDirectory: isDirectory) == expected
            },
            success: "PDF, spreadsheet, Word, presentation, archive, folder, and generic records map correctly",
            failure: "At least one file type mapped to the wrong icon category"
        ))
        results.append(check(
            name: "file type icons remain visually distinct",
            condition: Set(FileTypeIconCategory.allCases.map(\.symbolName)).count == FileTypeIconCategory.allCases.count,
            success: "Every file category uses a distinct SF Symbol",
            failure: "Two or more file categories share the same symbol"
        ))
        let fileTypePalettes = FileTypeIconCategory.allCases.map { ($0, AppTheme.fileTypePalette($0)) }
        let colorsArePairwiseDistinct = fileTypePalettes.indices.allSatisfy { leftIndex in
            fileTypePalettes.indices.dropFirst(leftIndex + 1).allSatisfy { rightIndex in
                let left = fileTypePalettes[leftIndex].1
                let right = fileTypePalettes[rightIndex].1
                return (left.lightBackground != right.lightBackground || left.lightForeground != right.lightForeground)
                    && (left.darkBackground != right.darkBackground || left.darkForeground != right.darkForeground)
            }
        }
        let genericSpreadsheetDistanceIsSufficient: Bool = {
            let generic = AppTheme.fileTypePalette(.generic)
            let spreadsheet = AppTheme.fileTypePalette(.spreadsheet)
            let distance: (SIMD3<Double>, SIMD3<Double>) -> Double = { left, right in
                max(
                    abs(left.x - right.x),
                    abs(left.y - right.y),
                    abs(left.z - right.z)
                )
            }
            return distance(generic.lightBackground, spreadsheet.lightBackground) >= 0.08
                && distance(generic.darkBackground, spreadsheet.darkBackground) >= 0.08
        }()
        results.append(check(
            name: "file type icon colors are pairwise distinct",
            condition: colorsArePairwiseDistinct && genericSpreadsheetDistanceIsSufficient,
            success: "Every file category has a distinct color pair and generic stays at least 0.08 from spreadsheet",
            failure: "Two file categories share a color pair or generic is too close to spreadsheet"
        ))

        let directoryCache = FileTypeIconDirectoryCache()
        var directoryLoadCount = 0
        let firstDirectoryValue = directoryCache.resolve(path: "/tmp/cached-folder") { _ in
            directoryLoadCount += 1
            return true
        }
        let secondDirectoryValue = directoryCache.resolve(path: "/tmp/cached-folder") { _ in
            directoryLoadCount += 1
            return false
        }
        results.append(check(
            name: "file type directory metadata is cached per path",
            condition: firstDirectoryValue && secondDirectoryValue && directoryLoadCount == 1,
            success: "The same path performs at most one directory metadata lookup",
            failure: "Directory detection repeated I/O for an already cached path"
        ))
        results.append(check(
            name: "image preview zoom clamps and resets",
            condition: ImagePreviewZoom.clamped(0.1) == 0.5
                && ImagePreviewZoom.clamped(0.5) == 0.5
                && ImagePreviewZoom.clamped(2.0) == 2.0
                && ImagePreviewZoom.clamped(4.0) == 4.0
                && ImagePreviewZoom.clamped(8.0) == 4.0
                && ImagePreviewZoom.stepped(from: 3.9, direction: 1) == 4.0
                && ImagePreviewZoom.stepped(from: 0.6, direction: -1) == 0.5
                && ImagePreviewZoom.resetValue == 1.0,
            success: "Image zoom stays within 50%-400% and reset returns to fit-window 100%",
            failure: "Image zoom exceeded its bounds or reset did not return 100%"
        ))
        results.append(check(
            name: "image OCR language configuration is local and corrected",
            condition: ImageTextRecognitionRules.recognitionLanguages == ["zh-Hans", "en-US"]
                && ImageTextRecognitionRules.usesLanguageCorrection,
            success: "OCR uses Simplified Chinese and English with language correction",
            failure: "OCR language or correction configuration is incomplete"
        ))

        let repeatedBox = CGRect(x: 0.1, y: 0.7, width: 0.3, height: 0.1)
        let aggregatedOCR = ImageTextRecognitionRules.aggregate([
            ImageRecognizedTextRegion(text: "  第一行  ", boundingBox: repeatedBox, confidence: 0.7),
            ImageRecognizedTextRegion(text: "第一行", boundingBox: repeatedBox, confidence: 0.95),
            ImageRecognizedTextRegion(text: "第二行", boundingBox: CGRect(x: 0.1, y: 0.4, width: 0.3, height: 0.1), confidence: 0.9),
            ImageRecognizedTextRegion(text: "   ", boundingBox: .zero, confidence: 1)
        ])
        results.append(check(
            name: "image OCR aggregates, orders, and deduplicates text",
            condition: aggregatedOCR.count == 2
                && aggregatedOCR.map(\.text) == ["第一行", "第二行"]
                && aggregatedOCR.first?.confidence == 0.95,
            success: "OCR removes blank and duplicate regions while preserving reading order and best confidence",
            failure: "OCR text aggregation produced duplicates, blanks, or the wrong reading order"
        ))

        let utf8BOMText = "UTF-8 中文"
        let utf8BOMData = Data([0xEF, 0xBB, 0xBF]) + (utf8BOMText.data(using: .utf8) ?? Data())
        let utf16LEText = "UTF-16 中文"
        let utf16LEData = Data([0xFF, 0xFE]) + (utf16LEText.data(using: .utf16LittleEndian) ?? Data())
        let utf16BEText = "大端文本"
        let utf16BEData = Data([0xFE, 0xFF]) + (utf16BEText.data(using: .utf16BigEndian) ?? Data())
        let gb18030Data = Data([0xD6, 0xD0, 0xCE, 0xC4])
        results.append(check(
            name: "text encoding detector covers BOM UTF-8 UTF-16 and GB18030",
            condition: TextEncodingDetector.decode(utf8BOMData) == DecodedTextContent(
                text: utf8BOMText,
                encodingName: "UTF-8 BOM",
                usedFallback: false
            )
                && TextEncodingDetector.decode(utf16LEData).text == utf16LEText
                && TextEncodingDetector.decode(utf16LEData).encodingName == "UTF-16 LE"
                && TextEncodingDetector.decode(utf16BEData).text == utf16BEText
                && TextEncodingDetector.decode(utf16BEData).encodingName == "UTF-16 BE"
                && TextEncodingDetector.decode(gb18030Data).text == "中文"
                && TextEncodingDetector.decode(gb18030Data).encodingName == "GB18030",
            success: "Text decoding recognizes UTF-8 BOM, both UTF-16 byte orders, and GB18030 Chinese",
            failure: "At least one required text encoding decoded incorrectly"
        ))
        results.append(check(
            name: "built-in text preview extension routing is narrow",
            condition: ["txt", "md", "log", "json", "xml", "csv", "ini", "yaml", "swift", "py"].allSatisfy {
                TextEncodingDetector.isTextFile(URL(fileURLWithPath: "/tmp/file.\($0)"))
            }
                && !["pdf", "docx", "pptx", "xlsx", "png"].contains {
                    TextEncodingDetector.isTextFile(URL(fileURLWithPath: "/tmp/file.\($0)"))
                },
            success: "Common plain-text files use the built-in preview while documents and images retain existing routes",
            failure: "Text preview routing captured an unsupported document type or missed a required text extension"
        ))

        let packagedVersion = "1.4.1"
        results.append(check(
            name: "app version has one display source and safe development fallbacks",
            condition: AppVersionInfo.windowTitle(bundleVersion: packagedVersion) == "ClipShelf 1.4.1"
                && AppVersionInfo.windowTitle(bundleVersion: nil) == "ClipShelf"
                && AppVersionInfo.statusVersion(bundleVersion: packagedVersion) == packagedVersion
                && AppVersionInfo.statusVersion(bundleVersion: nil) == "dev",
            success: "Window title and status version share the bundle version while unbundled builds stay identifiable",
            failure: "Version display sources diverged or an unbundled build exposed a stale release version"
        ))

        results.append(check(
            name: "clear history confirmation covers empty single and multiple histories",
            condition: !ClearHistoryConfirmation.shouldConfirm(itemCount: 0)
                && ClearHistoryConfirmation.shouldConfirm(itemCount: 1)
                && ClearHistoryConfirmation.shouldConfirm(itemCount: 12)
                && ClearHistoryConfirmation.title(itemCount: 1).contains("这条")
                && ClearHistoryConfirmation.title(itemCount: 12).contains("全部")
                && ClearHistoryConfirmation.message(itemCount: 12).contains("12"),
            success: "Only nonempty histories request confirmation and the copy reflects single or multiple records",
            failure: "Clear-history confirmation did not distinguish empty, single, and multiple histories"
        ))
        results.append(check(
            name: "record context menu preserves existing action order and pin wording",
            condition: ClipRowMenu.orderedActions == [.copy, .paste, .pin, .delete]
                && ClipRowMenu.pinTitle(isPinned: false) == "置顶"
                && ClipRowMenu.pinTitle(isPinned: true) == "取消置顶",
            success: "The native row menu exposes copy, paste, pin, and delete in the expected order",
            failure: "The record context menu structure or pin title diverged from the row actions"
        ))

        _ = CFPreferencesAppSynchronize(suiteName as CFString)
        let unexpectedSelfTestDefaultsFiles = selfTestDefaultsFiles()
            .subtracting(allowedSelfTestDefaultsFiles)
        clearSelfTestDefaults()
        results.append(check(
            name: "self test defaults suite file count does not grow",
            condition: suiteName == fixedSuiteName && unexpectedSelfTestDefaultsFiles.isEmpty,
            success: "The isolated self-test defaults suite does not increase files in Library/Preferences",
            failure: "Unexpected self-test defaults domains: \(unexpectedSelfTestDefaultsFiles.sorted())"
        ))

        return results
    }

    private static func check(
        name: String,
        condition: Bool,
        success: String,
        failure: String
    ) -> SelfTestResult {
        SelfTestResult(name: name, passed: condition, detail: condition ? success : failure)
    }
}

@_cdecl("ClipShelfRunSharedSelfTests")
func runSharedSelfTestsForSwiftPM() -> Int32 {
    let results = ClipShelfSelfTest.run()
    for result in results {
        fputs("[\(result.passed ? "PASS" : "FAIL")] \(result.name): \(result.detail)\n", stderr)
    }
    return results.allSatisfy(\.passed) ? 0 : 1
}

enum SelfTestCommand {
    static func runIfRequested(arguments: [String] = CommandLine.arguments) -> Int32? {
        guard arguments.dropFirst().first == "--self-test" else {
            return nil
        }

        guard arguments.count == 3 else {
            fputs("Usage: ClipShelf --self-test <report-path>\n", stderr)
            return 2
        }

        let results = ClipShelfSelfTest.run()
        let reportURL = URL(fileURLWithPath: arguments[2], relativeTo: URL(fileURLWithPath: FileManager.default.currentDirectoryPath))
            .standardizedFileURL
        let report = markdownReport(results: results)

        do {
            try FileManager.default.createDirectory(
                at: reportURL.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            try report.write(to: reportURL, atomically: true, encoding: .utf8)
        } catch {
            fputs("ClipShelf self-test could not write report: \(error.localizedDescription)\n", stderr)
            return 1
        }

        for result in results {
            print("[\(result.passed ? "PASS" : "FAIL")] \(result.name): \(result.detail)")
        }
        print("Self-test report: \(reportURL.path)")
        return results.allSatisfy(\.passed) ? 0 : 1
    }

    private static func markdownReport(results: [SelfTestResult]) -> String {
        let passed = results.filter(\.passed).count
        var lines = [
            "# ClipShelf Self-Test Report",
            "",
            "Result: \(passed)/\(results.count) checks passed.",
            ""
        ]
        lines.append(contentsOf: results.map { result in
            "- [\(result.passed ? "x" : " ")] \(result.name): \(result.detail)"
        })
        lines.append("")
        return lines.joined(separator: "\n")
    }
}
