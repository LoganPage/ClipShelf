import AppKit
import Foundation
import ImageIO
import UniformTypeIdentifiers

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
        let mainViewSourceURL = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Views/MainView.swift")
        let mainViewSource = try? String(contentsOf: mainViewSourceURL, encoding: .utf8)
        let runningFromAppBundle = Bundle.main.bundlePath.hasSuffix(".app")
        results.append(check(
            name: "record context menu exposes the declared actions in order",
            condition: ClipRowMenu.orderedActions == [.copy, .pin, .delete]
                && ClipRowMenu.title(for: .copy) == "复制"
                && ClipRowMenu.title(for: .delete) == "删除"
                && ClipRowMenu.title(for: .pin) == ClipRowMenu.pinTitle(isPinned: false)
                && ClipRowMenu.pinTitle(isPinned: false) == "置顶"
                && ClipRowMenu.pinTitle(isPinned: true) == "取消置顶"
                && (mainViewSource.map(ClipRowMenu.isDeclaredMenuInstalled(in:)) ?? runningFromAppBundle),
            success: "The installed native row menu derives copy, pin, and destructive delete from one declaration",
            failure: "The record context menu is missing, detached from its declaration, lacks a destructive delete role, or has divergent titles"
        ))

        let cleanupNow = Date(timeIntervalSince1970: 2_000_000_000)
        let cleanupCutoff = OldHistoryCleanup.cutoffDate(now: cleanupNow, retentionDays: 30)
        let pinnedOldItem = ClipItem(
            kind: .text,
            title: "pinned-old",
            createdAt: cleanupCutoff.addingTimeInterval(-1),
            isPinned: true
        )
        let unpinnedNewItem = ClipItem(
            kind: .text,
            title: "unpinned-new",
            createdAt: cleanupCutoff.addingTimeInterval(1)
        )
        let unpinnedOldItem = ClipItem(
            kind: .text,
            title: "unpinned-old",
            createdAt: cleanupCutoff.addingTimeInterval(-1)
        )
        let unpinnedAtCutoffItem = ClipItem(
            kind: .text,
            title: "unpinned-at-cutoff",
            createdAt: cleanupCutoff
        )
        let cleanupEligibleIDs = OldHistoryCleanup.eligibleIDs(
            in: [pinnedOldItem, unpinnedNewItem, unpinnedOldItem, unpinnedAtCutoffItem],
            now: cleanupNow,
            retentionDays: 30
        )
        results.append(check(
            name: "old history cleanup selects only unpinned records older than the window",
            condition: cleanupEligibleIDs == [unpinnedOldItem.id],
            success: "Only an unpinned record strictly older than the cutoff is eligible",
            failure: "Old-history eligibility included a pinned, recent, or cutoff-boundary record"
        ))

        results.append(check(
            name: "old history cleanup retention window only accepts the offered values",
            condition: OldHistoryCleanup.normalized(7) == 7
                && OldHistoryCleanup.normalized(30) == 30
                && OldHistoryCleanup.normalized(90) == 90
                && [0, 15, 999, -1].allSatisfy {
                    OldHistoryCleanup.normalized($0) == OldHistoryCleanup.defaultRetentionDays
                }
                && OldHistoryCleanup.load(from: defaults) == OldHistoryCleanup.defaultRetentionDays
                && OldHistoryCleanup.defaultRetentionDays == 30
                && OldHistoryCleanup.retentionOptions == [7, 30, 90]
                && OldHistoryCleanup.normalized(15) == 30,
            success: "Retention accepts 7, 30, or 90 days and otherwise uses the 30-day default",
            failure: "Retention accepted an unsupported value or did not use the default"
        ))

        let emptyCleanupSummary = OldHistoryCleanup.summaryText(eligibleCount: 0, retentionDays: 30)
        let populatedCleanupSummary = OldHistoryCleanup.summaryText(eligibleCount: 12, retentionDays: 30)
        results.append(check(
            name: "old history cleanup summary reports the eligible count",
            condition: emptyCleanupSummary == "没有符合条件的旧记录。"
                && populatedCleanupSummary.contains("30")
                && populatedCleanupSummary.contains("12"),
            success: "Cleanup summaries distinguish an empty result and include both days and item count",
            failure: "Cleanup summary omitted the empty state, retention days, or eligible count"
        ))

        let installedRowMenuSource = #"""
        private struct ClipRow: View {
            var body: some View {
                Text("row")
                    .contextMenu {
                        ForEach(ClipRowMenu.orderedActions, id: \.self) { action in
                            switch action {
                            case .copy:
                                Button("Copy") {}
                            case .pin:
                                Button("Pin") {}
                            case .delete:
                                Button("Delete", role: .destructive) {}
                            }
                        }
                    }
            }
        }
        """#
        let reflowedRowMenuSource = #"""
        private struct ClipRow: View {
            var body: some View {
                Text("row")
                    .contextMenu {
                        ForEach(
                            ClipRowMenu.orderedActions,
                            id: \.self
                        ) { action in
                            switch action {
                            case .copy:
                                Button("Copy") {}
                            case .pin:
                                Button("Pin") {}
                            case .delete:
                                Button("Delete", role:   .destructive) {}
                            }
                        }
                    }
            }
        }
        """#
        let wrongTypeRowMenuSource = #"""
        private struct ClipRowMenu: View {
            var body: some View {
                Text("decoy")
                    .contextMenu {
                        ForEach(ClipRowMenu.orderedActions, id: \.self) { action in
                            switch action {
                            case .delete:
                                Button("Delete", role: .destructive) {}
                            default:
                                EmptyView()
                            }
                        }
                    }
            }
        }
        private struct ClipRow: View {
            var body: some View { Text("real row") }
        }
        """#
        let stringDecoyRowMenuSource = #"""
        // private struct ClipRow: View { var body: some View { Text("comment decoy") } }
        private struct ClipRow: View {
            let decoy = ".contextMenu { ForEach(ClipRowMenu.orderedActions) { case .delete: Button(role: .destructive) } }"
            var body: some View { Text("row") }
        }
        """#
        let missingDeclarationRowMenuSource = #"""
        private struct ClipRow: View {
            var body: some View {
                Text("row")
                    .contextMenu {
                        let action = ClipRowMenuAction.delete
                        switch action {
                        case .copy:
                            Button("Copy") {}
                        case .pin:
                            Button("Pin") {}
                        case .delete:
                            Button("Delete", role: .destructive) {}
                        }
                    }
            }
        }
        """#
        let acceptedRowMenuCases = [
            ("installed menu", ClipRowMenu.isDeclaredMenuInstalled(in: installedRowMenuSource)),
            ("reflowed menu", ClipRowMenu.isDeclaredMenuInstalled(in: reflowedRowMenuSource)),
            ("wrong type boundary", !ClipRowMenu.isDeclaredMenuInstalled(in: wrongTypeRowMenuSource)),
            ("string literal decoy", !ClipRowMenu.isDeclaredMenuInstalled(in: stringDecoyRowMenuSource)),
            ("missing shared declaration", !ClipRowMenu.isDeclaredMenuInstalled(in: missingDeclarationRowMenuSource))
        ]
        let failedAcceptedRowMenuCases = acceptedRowMenuCases
            .filter { !$0.1 }
            .map(\.0)
        results.append(check(
            name: "row menu source check accepts the installed menu and tolerates reflow",
            condition: failedAcceptedRowMenuCases.isEmpty,
            success: "The source check accepts the installed form and whitespace reflow while rejecting type and string decoys",
            failure: "Failed source-check cases: \(failedAcceptedRowMenuCases.joined(separator: ", "))"
        ))

        let blockCommentedRowMenuSource = "/*\n\(installedRowMenuSource)\n*/"
        let lineCommentedRowMenuSource = installedRowMenuSource
            .split(separator: "\n", omittingEmptySubsequences: false)
            .map { "// \($0)" }
            .joined(separator: "\n")
        let duplicateRowMenuSource = #"""
        private struct ClipRow: View {
            var body: some View {
                Text("row")
                    .contextMenu {
                        ForEach(ClipRowMenu.orderedActions, id: \.self) { action in
                            switch action {
                            case .copy:
                                Button("Copy") {}
                            case .pin:
                                Button("Pin") {}
                            case .delete:
                                Button("Delete", role: .destructive) {}
                            }
                        }
                    }
                    .contextMenu {
                        ForEach(ClipRowMenu.orderedActions, id: \.self) { action in
                            switch action {
                            case .copy:
                                Button("Copy") {}
                            case .pin:
                                Button("Pin") {}
                            case .delete:
                                Button("Delete", role: .destructive) {}
                            }
                        }
                    }
            }
        }
        """#
        let misplacedRoleRowMenuSource = installedRowMenuSource
            .replacingOccurrences(of: #"Button("Pin") {}"#, with: #"Button("Pin", role: .destructive) {}"#)
            .replacingOccurrences(of: #"Button("Delete", role: .destructive) {}"#, with: #"Button("Delete") {}"#)
        let missingRoleRowMenuSource = installedRowMenuSource
            .replacingOccurrences(of: #", role: .destructive"#, with: "")
        let rejectedRowMenuCases = [
            ("block-commented menu", !ClipRowMenu.isDeclaredMenuInstalled(in: blockCommentedRowMenuSource)),
            ("line-commented menu", !ClipRowMenu.isDeclaredMenuInstalled(in: lineCommentedRowMenuSource)),
            ("duplicate menu decoy", !ClipRowMenu.isDeclaredMenuInstalled(in: duplicateRowMenuSource)),
            ("destructive role on pin", !ClipRowMenu.isDeclaredMenuInstalled(in: misplacedRoleRowMenuSource)),
            ("missing destructive role", !ClipRowMenu.isDeclaredMenuInstalled(in: missingRoleRowMenuSource)),
            ("missing shared declaration", !ClipRowMenu.isDeclaredMenuInstalled(in: missingDeclarationRowMenuSource))
        ]
        let failedRejectedRowMenuCases = rejectedRowMenuCases
            .filter { !$0.1 }
            .map(\.0)
        results.append(check(
            name: "row menu source check rejects comments decoys and misplaced roles",
            condition: failedRejectedRowMenuCases.isEmpty,
            success: "Comments, duplicate menus, misplaced roles, and hand-written actions are rejected",
            failure: "Failed rejection cases: \(failedRejectedRowMenuCases.joined(separator: ", "))"
        ))

        let absoluteCleanupNow = Date(timeIntervalSince1970: 2_000_000_000)
        results.append(check(
            name: "old history cleanup cutoff matches the declared retention window exactly",
            condition: OldHistoryCleanup.cutoffDate(now: absoluteCleanupNow, retentionDays: 30)
                == Date(timeIntervalSince1970: TimeInterval(2_000_000_000 - 30 * 86_400))
                && OldHistoryCleanup.cutoffDate(now: absoluteCleanupNow, retentionDays: 7)
                == Date(timeIntervalSince1970: TimeInterval(2_000_000_000 - 7 * 86_400))
                && OldHistoryCleanup.cutoffDate(now: absoluteCleanupNow, retentionDays: 90)
                == Date(timeIntervalSince1970: TimeInterval(2_000_000_000 - 90 * 86_400))
                && OldHistoryCleanup.cutoffDate(now: absoluteCleanupNow, retentionDays: 0)
                == OldHistoryCleanup.cutoffDate(now: absoluteCleanupNow, retentionDays: 30),
            success: "The 7-, 30-, and 90-day cutoffs match their exact durations and invalid values fall back to 30 days",
            failure: "A cleanup cutoff drifted from its declared duration or invalid values did not fall back to 30 days"
        ))

        let clipStoreSourceURL = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("Stores/ClipStore.swift")
        let clipStoreSource = try? String(contentsOf: clipStoreSourceURL, encoding: .utf8)
        let oldHistoryRemovalBody = clipStoreSource.flatMap {
            SourceScan.body(
                ofFunctionNamed: "removeOldUnpinnedItems",
                in: SourceScan.codeOnly($0)
            )
        }
        let oldHistoryRemovalIsUndoable = oldHistoryRemovalBody.map {
            SourceScan.contains(#"remove\(ids:"#, in: $0)
                && !SourceScan.contains(#"items\.removeAll"#, in: $0)
                && !SourceScan.contains(#"deletionUndoStack"#, in: $0)
                && !SourceScan.contains(#"save\(\)"#, in: $0)
        } ?? runningFromAppBundle
        results.append(check(
            name: "old history cleanup routes through the shared removal path so it stays undoable",
            condition: oldHistoryRemovalIsUndoable,
            success: "Old-history cleanup delegates to the shared undoable removal path",
            failure: "Old-history cleanup bypasses remove(ids:) or directly mutates, records, or saves history"
        ))

        let scrollFixtureStart = Date(timeIntervalSince1970: 1_700_000_000)
        let olderPinnedScrollItem = ClipItem(
            kind: .text,
            title: "older-pinned",
            createdAt: scrollFixtureStart,
            isPinned: true
        )
        let newestScrollItem = ClipItem(
            kind: .text,
            title: "newest-unpinned",
            createdAt: scrollFixtureStart.addingTimeInterval(60)
        )
        let pinSortedScrollItems = [olderPinnedScrollItem, newestScrollItem]
        results.append(check(
            name: "newest record anchor ignores pin order",
            condition: HistoryScrollTarget.newestAnchorID(in: pinSortedScrollItems) == newestScrollItem.id
                && HistoryScrollTarget.newestAnchorID(in: pinSortedScrollItems) != pinSortedScrollItems.first?.id,
            success: "The newest timestamp supplies the scroll anchor even when an older pinned record sorts first",
            failure: "The scroll anchor followed pin order instead of the newest timestamp"
        ))

        var newlyPinnedScrollItem = newestScrollItem
        newlyPinnedScrollItem.isPinned = true
        let lastRevealedNewest = newestScrollItem.createdAt
        results.append(check(
            name: "scroll anchor stays nil unless the newest record is newer",
            condition: HistoryScrollTarget.anchorToReveal(
                in: pinSortedScrollItems,
                lastRevealedNewest: olderPinnedScrollItem.createdAt
            ) == newestScrollItem.id
                && HistoryScrollTarget.anchorToReveal(
                    in: pinSortedScrollItems,
                    lastRevealedNewest: lastRevealedNewest
                ) == nil
                && HistoryScrollTarget.anchorToReveal(
                    in: [olderPinnedScrollItem],
                    lastRevealedNewest: lastRevealedNewest
                ) == nil
                && HistoryScrollTarget.anchorToReveal(
                    in: [newlyPinnedScrollItem, olderPinnedScrollItem],
                    lastRevealedNewest: lastRevealedNewest
                ) == nil,
            success: "Only a timestamp newer than the last revealed record requests scrolling",
            failure: "Deletion, pin reordering, or an unchanged newest timestamp requested scrolling"
        ))

        HistoryFilterPreferences.save(.image, to: defaults)
        let filterReloadedDefaults = UserDefaults(suiteName: suiteName)
        results.append(check(
            name: "history filter preference round trips through defaults",
            condition: filterReloadedDefaults.map { HistoryFilterPreferences.load(from: $0) } == .image,
            success: "The selected history filter survives a fresh defaults instance",
            failure: "The selected history filter was not persisted"
        ))

        defaults.removeObject(forKey: HistoryFilterPreferences.key)
        let emptyFilterValue = HistoryFilterPreferences.load(from: defaults)
        defaults.set("bogus-not-a-kind", forKey: HistoryFilterPreferences.key)
        let invalidFilterValue = HistoryFilterPreferences.load(from: defaults)
        results.append(check(
            name: "unknown history filter value falls back to all",
            condition: HistoryFilterPreferences.defaultValue == .all
                && emptyFilterValue == .all
                && invalidFilterValue == .all,
            success: "Missing and unknown filter values both fall back to all records",
            failure: "A missing or unknown filter value did not fall back to all records"
        ))

        let unselectedRestBackground = IconButtonAppearance.background(
            isSelected: false,
            isPressed: false,
            isHovered: false
        )
        let unselectedHoverBackground = IconButtonAppearance.background(
            isSelected: false,
            isPressed: false,
            isHovered: true
        )
        let selectedRestBackground = IconButtonAppearance.background(
            isSelected: true,
            isPressed: false,
            isHovered: false
        )
        let selectedHoverBackground = IconButtonAppearance.background(
            isSelected: true,
            isPressed: false,
            isHovered: true
        )
        results.append(check(
            name: "icon button hover surface differs from the rest state",
            condition: unselectedHoverBackground != unselectedRestBackground
                && selectedHoverBackground != selectedRestBackground,
            success: "Selected and unselected icon buttons both expose a distinct hover surface",
            failure: "At least one icon-button hover surface matches its resting state"
        ))

        results.append(check(
            name: "icon button press state outranks hover",
            condition: IconButtonAppearance.background(
                isSelected: false,
                isPressed: true,
                isHovered: true
            ) == IconButtonAppearance.background(
                isSelected: false,
                isPressed: true,
                isHovered: false
            ),
            success: "The pressed surface is independent of hover state",
            failure: "Hover changed the surface while the icon button was pressed"
        ))

        results.append(check(
            name: "selection row transition duration is a pinned literal",
            condition: SelectionRowMotion.duration == 0.11,
            success: "Selection rows use the pinned 0.11-second transition",
            failure: "The selection-row transition duration drifted from 0.11 seconds"
        ))

        results.append(check(
            name: "selection row surface opacity keeps the existing presentation",
            condition: SelectionRowMotion.surfaceOpacity(isSelected: false, isDark: true, isPreset: false) == 0
                && SelectionRowMotion.surfaceOpacity(isSelected: false, isDark: true, isPreset: true) == 0
                && SelectionRowMotion.surfaceOpacity(isSelected: false, isDark: false, isPreset: false) == 0
                && SelectionRowMotion.surfaceOpacity(isSelected: true, isDark: true, isPreset: false) == 0.68
                && SelectionRowMotion.surfaceOpacity(isSelected: true, isDark: true, isPreset: true) == 1
                && SelectionRowMotion.surfaceOpacity(isSelected: true, isDark: false, isPreset: false) == 1,
            success: "Selection opacity preserves clear rest, dark custom 0.68, and all other selected surfaces at 1",
            failure: "Selection opacity changed the existing light, dark, preset, or custom presentation"
        ))

        let clipRowSurfaceAnimationIsScoped = mainViewSource.map { source in
            guard let clipRowBody = SourceScan.body(ofTypeNamed: "ClipRow", in: source) else {
                return false
            }
            let code = SourceScan.codeOnly(clipRowBody)
            let animationExpression = try? NSRegularExpression(pattern: #"\.animation\s*\("#)
            let range = NSRange(code.startIndex..<code.endIndex, in: code)
            let animationCount = animationExpression?.numberOfMatches(in: code, range: range) ?? 0
            let surfacePattern = #"\.background\s*\{(?:(?!\n\s*\})[\s\S])*?SelectionRowMotion\.surfaceOpacity(?:(?!\n\s*\})[\s\S])*?\.animation\s*\("#
            let hStackRange = code.range(of: #"\bHStack\s*\("#, options: .regularExpression)
            let animationRange = code.range(of: #"\.animation\s*\("#, options: .regularExpression)
            return animationCount == 1
                && SourceScan.contains(surfacePattern, in: code)
                && (hStackRange?.lowerBound ?? code.endIndex) < (animationRange?.lowerBound ?? code.startIndex)
        } ?? runningFromAppBundle
        results.append(check(
            name: "selection row animation only targets the surface layer",
            condition: SelectionRowMotion.animation == .easeOut(duration: SelectionRowMotion.duration)
                && clipRowSurfaceAnimationIsScoped,
            success: "The single selection animation is an ease-out transition scoped to the background surface",
            failure: "The selection animation changed curve or escaped the background surface layer"
        ))

        let searchEquivalenceFixtures: [(String, ClipItem)] = [
            (
                "chinese-title",
                ClipItem(kind: .text, title: "李永乐讲比特币", text: "课程摘要")
            ),
            (
                "long-text",
                ClipItem(
                    kind: .text,
                    title: "Meeting Notes",
                    text: String(repeating: "这是一段用于搜索索引等价验证的长文本。", count: 300) + " project nebula"
                )
            ),
            (
                "file-path",
                ClipItem(kind: .file, title: "Document", filePaths: ["/tmp/BTC-Résumé.PDF"])
            ),
            (
                "source-path",
                ClipItem(kind: .image, title: "Picture", sourcePath: "/Pictures/截图😀Ｆｕｌｌ.png")
            ),
            (
                "emoji-fullwidth-diacritic",
                ClipItem(kind: .text, title: "✨ Ｃａｆé НАБОР", text: "Emoji 😀 and naïve façade")
            ),
            (
                "negative-control",
                ClipItem(kind: .text, title: "Completely unrelated", text: "quiet archive")
            )
        ]
        let searchEquivalenceQueries = [
            "", "liyongle", "lyl", "ygl", "比特", "Meeting Notes", "metingnotes",
            "PROJECTNEBULA", "ｂｔｃ", "resume", "截图", "full", "cafe", "naive", "not-present"
        ]
        var searchEquivalenceMismatch: String?
        for (fixtureName, item) in searchEquivalenceFixtures where searchEquivalenceMismatch == nil {
            for query in searchEquivalenceQueries {
                let indexed = SearchMatcher.matches(item, query: query)
                let unindexed = SearchMatcher.matchesUnindexed(item, query: query)
                if indexed != unindexed {
                    searchEquivalenceMismatch = "fixture=\(fixtureName), query=\(query), indexed=\(indexed), unindexed=\(unindexed)"
                    break
                }
            }
        }
        if searchEquivalenceMismatch == nil {
            for filter in ClipKindFilter.allCases where searchEquivalenceMismatch == nil {
                for query in searchEquivalenceQueries {
                    let indexedIDs = ClipHistoryFilter.items(
                        searchEquivalenceFixtures.map(\.1),
                        kind: filter,
                        query: query
                    ).map(\.id)
                    let unindexedIDs = searchEquivalenceFixtures.map(\.1).filter {
                        filter.matches($0) && SearchMatcher.matchesUnindexed($0, query: query)
                    }.map(\.id)
                    if indexedIDs != unindexedIDs {
                        searchEquivalenceMismatch = "fixture=kind-filter-\(filter.rawValue), query=\(query)"
                        break
                    }
                }
            }
        }
        results.append(check(
            name: "search index path stays equivalent to the unindexed path for pinyin queries",
            condition: searchEquivalenceMismatch == nil,
            success: "Every fixture and query matches identically through indexed and unindexed paths",
            failure: "Search path mismatch: \(searchEquivalenceMismatch ?? "unknown fixture/query")"
        ))

        let reuseIndexCache = ClipSearchIndexCache(maximumEntryCount: 2)
        let reuseItem = ClipItem(kind: .image, title: "索引复用", imageData: Data([1, 2, 3]))
        let firstReuseIndex = reuseIndexCache.index(for: reuseItem)
        var imageOnlyChange = reuseItem
        imageOnlyChange.imageData = Data([9, 8, 7, 6])
        let secondReuseIndex = reuseIndexCache.index(for: imageOnlyChange)
        results.append(check(
            name: "search index is reused across queries for the same record",
            condition: reuseIndexCache.buildCount == 1 && firstReuseIndex == secondReuseIndex,
            success: "Repeated access and image-only changes reuse one searchable index",
            failure: "The same searchable fields rebuilt the index \(reuseIndexCache.buildCount) times"
        ))

        let rebuildIndexCache = ClipSearchIndexCache(maximumEntryCount: 2)
        var replaceableItem = ClipItem(kind: .text, title: "Mutable", text: "original text")
        _ = rebuildIndexCache.index(for: replaceableItem)
        replaceableItem.text = "李永乐 replacement text"
        let rebuiltIndex = rebuildIndexCache.index(for: replaceableItem)
        results.append(check(
            name: "search index rebuilds when a record's searchable text changes",
            condition: rebuildIndexCache.buildCount == 2
                && SearchMatcher.matches(rebuiltIndex, query: "liyongle"),
            success: "Replacing searchable text invalidates and rebuilds the cached index",
            failure: "A searchable-text replacement reused stale index content"
        ))

        let boundedIndexCache = ClipSearchIndexCache(maximumEntryCount: 3)
        for number in 0..<8 {
            _ = boundedIndexCache.index(for: ClipItem(kind: .text, title: "bounded-\(number)"))
        }
        results.append(check(
            name: "search index cache stays within its bound",
            condition: boundedIndexCache.count == 3 && boundedIndexCache.buildCount == 8,
            success: "Least-recently-used eviction keeps the index cache at its configured limit",
            failure: "Index cache count was \(boundedIndexCache.count) instead of 3"
        ))

        let writerURL = temporaryRoot.appendingPathComponent("writer/coalesced-history.json")
        let writer = HistoryWriter(destinationURL: writerURL, coalescingWindow: 0.05)
        let writerSnapshots = (0..<5).map { number in
            [ClipItem(kind: .text, title: "snapshot-\(number)", text: "snapshot-\(number)")]
        }
        for snapshot in writerSnapshots {
            writer.schedule(snapshot)
        }
        writer.flush()
        let coalescedData = try? Data(contentsOf: writerURL)
        let coalescedItems = coalescedData.flatMap { try? JSONDecoder().decode([ClipItem].self, from: $0) }
        writer.flush()
        results.append(check(
            name: "history writer coalesces saves and flushes the latest snapshot",
            condition: writer.writeCount == 1 && coalescedItems == writerSnapshots.last,
            success: "Five schedules produce one atomic write, flush persists the latest snapshot, and repeated flush is idempotent",
            failure: "Writer produced \(writer.writeCount) writes or did not persist the fifth snapshot"
        ))

        let identicalWriterURL = temporaryRoot.appendingPathComponent("writer/identical-history.json")
        let identicalWriter = HistoryWriter(destinationURL: identicalWriterURL, coalescingWindow: 0.05)
        let identicalItems = [
            ClipItem(kind: .text, title: "byte-identical", text: "字节一致"),
            ClipItem(kind: .file, title: "report.pdf", filePaths: ["/tmp/report.pdf"])
        ]
        let directEncoder = JSONEncoder()
        directEncoder.outputFormatting = [.sortedKeys]
        let directEncodedItems = try? directEncoder.encode(identicalItems)
        identicalWriter.schedule(identicalItems)
        identicalWriter.flush()
        let writerEncodedItems = try? Data(contentsOf: identicalWriterURL)
        results.append(check(
            name: "history writer persists bytes identical to a direct encode",
            condition: directEncodedItems != nil && writerEncodedItems == directEncodedItems,
            success: "Background atomic persistence is byte-for-byte identical to direct JSONEncoder output",
            failure: "Background writer changed history JSON bytes or failed to write"
        ))

        let generatedEncoderTIFF: Data = {
            guard let context = CGContext(
                data: nil,
                width: 12,
                height: 7,
                bitsPerComponent: 8,
                bytesPerRow: 12 * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
            ) else { return Data() }
            context.setFillColor(CGColor(red: 0.2, green: 0.8, blue: 0.6, alpha: 1))
            context.fill(CGRect(x: 0, y: 0, width: 12, height: 7))
            guard let image = context.makeImage() else { return Data() }
            let output = NSMutableData()
            guard let destination = CGImageDestinationCreateWithData(
                output,
                UTType.tiff.identifier as CFString,
                1,
                nil
            ) else { return Data() }
            CGImageDestinationAddImage(destination, image, nil)
            guard CGImageDestinationFinalize(destination) else { return Data() }
            return output as Data
        }()
        let encodedPNG = ImageCaptureEncoder.pngData(fromTIFF: generatedEncoderTIFF)
        let decodedEncoderImage = encodedPNG.flatMap { data in
            CGImageSourceCreateWithData(data as CFData, nil)
        }.flatMap { source in
            CGImageSourceCreateImageAtIndex(source, 0, nil)
        }
        let encoderRunsOffMainThread = clipStoreSource.map { source in
            SourceScan.contains(#"imageEncodingQueue\.async"#, in: SourceScan.codeOnly(source))
        } ?? runningFromAppBundle
        results.append(check(
            name: "image capture encoder reuses png bytes without touching the main thread",
            condition: encoderRunsOffMainThread
                && encodedPNG != nil
                && decodedEncoderImage?.width == 12
                && decodedEncoderImage?.height == 7
                && ImageCaptureEncoder.pngData(fromTIFF: Data([0x00, 0x01, 0x02])) == nil,
            success: "TIFF encoding runs off-main, preserves dimensions, yields decodable PNG, and rejects damaged data",
            failure: "Encoder details: tiff=\(generatedEncoderTIFF.count), png=\(encodedPNG?.count ?? -1), size=\(decodedEncoderImage?.width ?? -1)x\(decodedEncoderImage?.height ?? -1), async=\(encoderRunsOffMainThread), damaged=\(ImageCaptureEncoder.pngData(fromTIFF: Data([0x00, 0x01, 0x02])) != nil)"
        ))

        let thumbnailData = generatedEncoderTIFF
        let thumbnailCache = ImageThumbnailCache(maximumEntryCount: 1)
        let thumbnailItemID = UUID()
        let firstThumbnail = thumbnailCache.thumbnailSynchronouslyForTesting(for: thumbnailItemID, data: thumbnailData)
        let secondThumbnail = thumbnailCache.thumbnailSynchronouslyForTesting(for: thumbnailItemID, data: thumbnailData)
        _ = thumbnailCache.thumbnailSynchronouslyForTesting(for: UUID(), data: thumbnailData)
        results.append(check(
            name: "search thumbnail cache returns the same image twice without re-decoding",
            condition: firstThumbnail != nil
                && secondThumbnail != nil
                && firstThumbnail === secondThumbnail
                && thumbnailCache.decodeCount == 2
                && thumbnailCache.count == 1,
            success: "A repeated ID reuses its 104x80 thumbnail and LRU eviction honors the configured bound",
            failure: "Thumbnail details: data=\(thumbnailData.count), first=\(firstThumbnail != nil), second=\(secondThumbnail != nil), decoded=\(thumbnailCache.decodeCount), retained=\(thumbnailCache.count)"
        ))

        let screenshotWatcher = ScreenshotFolderWatcher.shared
        results.append(check(
            name: "screenshot folder watcher rescans on a slow fallback interval",
            condition: ScreenshotFolderWatcher.minimumRescanInterval >= 5
                && screenshotWatcher.isLikelyScreenshot(URL(fileURLWithPath: "/tmp/截屏 2026-10-02.png"))
                && screenshotWatcher.isLikelyScreenshot(URL(fileURLWithPath: "/tmp/Screenshot 2026-10-02.HEIC"))
                && !screenshotWatcher.isLikelyScreenshot(URL(fileURLWithPath: "/tmp/photo.png"))
                && !screenshotWatcher.isLikelyScreenshot(URL(fileURLWithPath: "/tmp/截屏 2026-10-02.txt")),
            success: "Event monitoring keeps a five-second fallback while screenshot name and extension rules stay intact",
            failure: "Fallback interval or screenshot eligibility changed"
        ))

        let fuzzyLiteralResults = [
            SearchMatcher.fuzzyContains("abcd", in: "abcx"),
            SearchMatcher.fuzzyContains("abcd", in: "abxx"),
            SearchMatcher.fuzzyContains("abcdef", in: "abcdxx"),
            SearchMatcher.fuzzyContains("abcdef", in: "abcxxx"),
            SearchMatcher.fuzzyContains("ab", in: "ab"),
            SearchMatcher.fuzzyContains("abc", in: "abc"),
            SearchMatcher.fuzzyContains("meeting", in: "xxmeetingxx"),
            SearchMatcher.fuzzyContains("zzzz", in: "meetingnotes"),
            SearchMatcher.fuzzyContains("metingnotes", in: "meetingnotes")
        ]
        results.append(check(
            name: "fuzzy search tolerance is pinned by literal examples",
            condition: SearchMatcher.fuzzyContains("abcd", in: "abcx") == true
                && SearchMatcher.fuzzyContains("abcd", in: "abxx") == false
                && SearchMatcher.fuzzyContains("abcdef", in: "abcdxx") == true
                && SearchMatcher.fuzzyContains("abcdef", in: "abcxxx") == false
                && SearchMatcher.fuzzyContains("ab", in: "ab") == false
                && SearchMatcher.fuzzyContains("abc", in: "abc") == true
                && SearchMatcher.fuzzyContains("meeting", in: "xxmeetingxx") == true
                && SearchMatcher.fuzzyContains("zzzz", in: "meetingnotes") == false
                && SearchMatcher.fuzzyContains("metingnotes", in: "meetingnotes") == true,
            success: "Short and long fuzzy tolerances, activation threshold, exact, negative, and edit examples stay pinned",
            failure: "Fuzzy literal results changed: \(fuzzyLiteralResults) expected [true, false, true, false, false, true, true, false, true]"
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
