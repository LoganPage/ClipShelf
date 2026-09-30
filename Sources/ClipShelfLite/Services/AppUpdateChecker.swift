import AppKit
import Foundation

@MainActor
final class AppUpdateChecker: ObservableObject {
    static let shared = AppUpdateChecker()

    @Published private(set) var availableUpdate: AvailableUpdate?
    @Published private(set) var isChecking = false

    private let latestReleaseURL = URL(string: "https://api.github.com/repos/Applebook743/ClipShelf/releases/latest")!
    private let releasesPageURL = URL(string: "https://github.com/Applebook743/ClipShelf/releases")!
    private var hasChecked = false

    private init() {}

    func checkIfNeeded() {
        guard !hasChecked else { return }
        hasChecked = true
        check()
    }

    func check() {
        guard !isChecking else { return }
        isChecking = true

        var request = URLRequest(url: latestReleaseURL)
        request.setValue("ClipShelf", forHTTPHeaderField: "User-Agent")
        request.cachePolicy = .reloadIgnoringLocalCacheData

        URLSession.shared.dataTask(with: request) { [weak self] data, response, error in
            Task { @MainActor in
                guard let self else { return }
                self.isChecking = false

                guard error == nil,
                      let httpResponse = response as? HTTPURLResponse,
                      (200..<300).contains(httpResponse.statusCode),
                      let data,
                      let release = try? JSONDecoder().decode(GitHubRelease.self, from: data) else {
                    self.availableUpdate = nil
                    return
                }

                let currentVersion = AppVersion.current
                let latestVersion = AppVersion(release.tagName)
                guard latestVersion > currentVersion else {
                    self.availableUpdate = nil
                    return
                }

                self.availableUpdate = AvailableUpdate(
                    versionText: release.tagName,
                    pageURL: release.htmlURL ?? self.releasesPageURL
                )
            }
        }.resume()
    }

    func openUpdatePage() {
        NSWorkspace.shared.open(availableUpdate?.pageURL ?? releasesPageURL)
    }
}

struct AvailableUpdate: Equatable {
    let versionText: String
    let pageURL: URL
}

private struct GitHubRelease: Decodable {
    let tagName: String
    let htmlURL: URL?

    enum CodingKeys: String, CodingKey {
        case tagName = "tag_name"
        case htmlURL = "html_url"
    }
}

private struct AppVersion: Comparable {
    static var current: AppVersion {
        AppVersion(AppVersionInfo.statusVersion(bundleVersion: AppVersionInfo.bundleVersion))
    }

    private let parts: [Int]

    init(_ rawValue: String) {
        let cleaned = rawValue
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
        parts = cleaned
            .split { character in
                character == "." || character == "-" || character == "_"
            }
            .map { Int($0) ?? 0 }
    }

    static func < (lhs: AppVersion, rhs: AppVersion) -> Bool {
        let count = max(lhs.parts.count, rhs.parts.count)
        for index in 0..<count {
            let left = index < lhs.parts.count ? lhs.parts[index] : 0
            let right = index < rhs.parts.count ? rhs.parts[index] : 0
            if left != right {
                return left < right
            }
        }
        return false
    }
}
