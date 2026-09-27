import Foundation

enum AppEnvironment {
    static let dataDirectory = dataDirectory(environment: ProcessInfo.processInfo.environment)
    static let userDefaults = userDefaults(environment: ProcessInfo.processInfo.environment)

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

    private static func nonemptyValue(for key: String, in environment: [String: String]) -> String? {
        guard let value = environment[key]?.trimmingCharacters(in: .whitespacesAndNewlines),
              !value.isEmpty else {
            return nil
        }
        return value
    }
}
