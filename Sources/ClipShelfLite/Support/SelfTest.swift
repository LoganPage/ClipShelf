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
