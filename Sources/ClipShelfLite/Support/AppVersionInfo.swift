import Foundation

enum AppVersionInfo {
    static var bundleVersion: String? {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
    }

    static func windowTitle(bundleVersion: String?) -> String {
        guard let version = normalized(bundleVersion) else { return "ClipShelf" }
        return "ClipShelf \(version)"
    }

    static func statusVersion(bundleVersion: String?) -> String {
        normalized(bundleVersion) ?? "dev"
    }

    private static func normalized(_ version: String?) -> String? {
        guard let version else { return nil }
        let trimmed = version.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
}
