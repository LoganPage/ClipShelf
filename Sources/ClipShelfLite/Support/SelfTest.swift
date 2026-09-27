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
        let suiteName = "ClipShelf.SelfTest.\(UUID().uuidString)"
        let environment = [
            "CLIPSHELF_DATA_DIR": temporaryRoot.path,
            "CLIPSHELF_DEFAULTS_SUITE": suiteName
        ]

        defer {
            try? fileManager.removeItem(at: temporaryRoot)
            UserDefaults.standard.removePersistentDomain(forName: suiteName)
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
