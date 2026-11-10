import AppKit
import Foundation

/// Looks for a newer macOS release on GitHub.
///
/// The repository holds both apps' releases, told apart by tag:
/// `macos-v2.0.0` here, `windows-v1.4.9` for Windows. Asking for the
/// repository's "latest" release used to return a Windows one, whose tag
/// never compared as newer, so the update button could not appear.
@MainActor
final class AppUpdateChecker: ObservableObject {
    static let shared = AppUpdateChecker()

    @Published private(set) var availableUpdate: AvailableUpdate?
    @Published private(set) var isChecking = false

    private let releasesURL = URL(string: "https://api.github.com/repos/LoganPage/ClipShelf/releases?per_page=30")!
    private let releasesPageURL = URL(string: "https://github.com/LoganPage/ClipShelf/releases")!
    private var lastCheck: Date?
    /// The app stays open for days: look again now and then.
    private static let checkInterval: TimeInterval = 6 * 60 * 60

    private init() {}

    func checkIfNeeded() {
        // An unbundled development build has no version to compare.
        guard AppVersionInfo.bundleVersion != nil else { return }
        if let lastCheck, Date().timeIntervalSince(lastCheck) < Self.checkInterval { return }
        check()
    }

    func check() {
        guard !isChecking else { return }
        isChecking = true
        lastCheck = Date()

        var request = URLRequest(url: releasesURL)
        request.setValue("ClipShelf", forHTTPHeaderField: "User-Agent")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.cachePolicy = .reloadIgnoringLocalCacheData

        URLSession.shared.dataTask(with: request) { [weak self] data, response, error in
            Task { @MainActor in
                guard let self else { return }
                self.isChecking = false

                // A failed check says nothing: what was found before stays.
                guard error == nil,
                      let httpResponse = response as? HTTPURLResponse,
                      (200..<300).contains(httpResponse.statusCode),
                      let data else {
                    return
                }
                self.availableUpdate = MacReleaseFeed.newestUpdate(
                    in: data,
                    currentVersion: AppVersionInfo.statusVersion(bundleVersion: AppVersionInfo.bundleVersion)
                )
            }
        }.resume()
    }

    #if CLIPSHELF_BENCH
    /// Shows the update button, to see how it looks.
    func benchOfferUpdate() {
        availableUpdate = AvailableUpdate(versionText: "9.9.9", pageURL: releasesPageURL)
    }
    #endif

    func openUpdatePage() {
        NSWorkspace.shared.open(availableUpdate?.pageURL ?? releasesPageURL)
    }
}

struct AvailableUpdate: Equatable {
    let versionText: String
    let pageURL: URL
}

/// Picks the macOS release to offer out of GitHub's list of releases.
enum MacReleaseFeed {
    static let tagPrefix = "macos-v"

    static func newestUpdate(in data: Data, currentVersion: String) -> AvailableUpdate? {
        guard let releases = try? JSONDecoder().decode([GitHubRelease].self, from: data),
              let current = AppVersion(currentVersion) else { return nil }
        let newest = releases
            .compactMap { release -> (version: AppVersion, release: GitHubRelease)? in
                guard release.draft != true, release.prerelease != true,
                      release.tagName.hasPrefix(tagPrefix),
                      let version = AppVersion(String(release.tagName.dropFirst(tagPrefix.count))) else { return nil }
                return (version, release)
            }
            .max { $0.version < $1.version }
        guard let newest, newest.version > current, let pageURL = newest.release.htmlURL else { return nil }
        return AvailableUpdate(versionText: newest.version.text, pageURL: pageURL)
    }

    struct GitHubRelease: Decodable {
        let tagName: String
        let htmlURL: URL?
        let draft: Bool?
        let prerelease: Bool?

        enum CodingKeys: String, CodingKey {
            case tagName = "tag_name"
            case htmlURL = "html_url"
            case draft, prerelease
        }
    }
}

/// A plain dotted version, like 2.0.0.
struct AppVersion: Comparable {
    private let parts: [Int]

    var text: String { parts.map(String.init).joined(separator: ".") }

    init?(_ rawValue: String) {
        let numbers = rawValue.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: ".", omittingEmptySubsequences: false)
        let parts = numbers.compactMap { Int($0) }
        guard !parts.isEmpty, parts.count == numbers.count, parts.allSatisfy({ $0 >= 0 }) else { return nil }
        self.parts = parts
    }

    static func == (lhs: AppVersion, rhs: AppVersion) -> Bool {
        !(lhs < rhs) && !(rhs < lhs)
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
