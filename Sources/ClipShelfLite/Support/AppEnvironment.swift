import AppKit
import Foundation

enum AppEnvironment {
    static let dataDirectory = dataDirectory(environment: ProcessInfo.processInfo.environment)
    static let userDefaults = userDefaults(environment: ProcessInfo.processInfo.environment)
    static let pasteboard = pasteboard(environment: ProcessInfo.processInfo.environment)

    static var historyURL: URL {
        dataDirectory.appendingPathComponent("history.json")
    }

    static func dataDirectory(
        environment: [String: String],
        fileManager: FileManager = .default
    ) -> URL {
        if let path = nonemptyValue(for: "CLIPSHELF_DATA_DIR", in: environment) {
            return URL(
                fileURLWithPath: NSString(string: path).expandingTildeInPath,
                isDirectory: true
            )
        }

        let support = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? fileManager.temporaryDirectory
        return support.appendingPathComponent("ClipShelf", isDirectory: true)
    }

    static func userDefaults(environment: [String: String]) -> UserDefaults {
        guard let suiteName = defaultsSuiteName(environment: environment),
              let defaults = UserDefaults(suiteName: suiteName) else {
            return .standard
        }
        return defaults
    }

    static func defaultsSuiteName(environment: [String: String]) -> String? {
        nonemptyValue(for: "CLIPSHELF_DEFAULTS_SUITE", in: environment)
    }

    static func pasteboard(environment: [String: String]) -> NSPasteboard {
        guard let name = pasteboardName(environment: environment) else {
            return .general
        }
        return NSPasteboard(name: NSPasteboard.Name(name))
    }

    static func pasteboardName(environment: [String: String]) -> String? {
        nonemptyValue(for: "CLIPSHELF_PASTEBOARD_NAME", in: environment)
    }

    static func controlSocketURL(
        environment: [String: String],
        fileManager: FileManager = .default
    ) throws -> URL? {
        guard let path = nonemptyValue(for: "CLIPSHELF_CONTROL_SOCKET", in: environment) else {
            return nil
        }

        guard nonemptyValue(for: "CLIPSHELF_DATA_DIR", in: environment) != nil,
              defaultsSuiteName(environment: environment) != nil,
              pasteboardName(environment: environment) != nil else {
            throw RuntimeControlError.invalidSocketPath(
                "Runtime control requires CLIPSHELF_DATA_DIR, CLIPSHELF_DEFAULTS_SUITE, and CLIPSHELF_PASTEBOARD_NAME"
            )
        }

        let socketURL = URL(
            fileURLWithPath: NSString(string: path).expandingTildeInPath
        ).standardizedFileURL
        let isolatedDirectory = dataDirectory(
            environment: environment,
            fileManager: fileManager
        ).standardizedFileURL
        let isolatedPrefix = isolatedDirectory.path.hasSuffix("/")
            ? isolatedDirectory.path
            : isolatedDirectory.path + "/"

        guard socketURL.path.hasPrefix(isolatedPrefix),
              socketURL.path != isolatedDirectory.path else {
            throw RuntimeControlError.invalidSocketPath(
                "CLIPSHELF_CONTROL_SOCKET must be inside CLIPSHELF_DATA_DIR"
            )
        }
        return socketURL
    }

    private static func nonemptyValue(for key: String, in environment: [String: String]) -> String? {
        guard let value = environment[key]?.trimmingCharacters(in: .whitespacesAndNewlines),
              !value.isEmpty else {
            return nil
        }
        return value
    }
}
