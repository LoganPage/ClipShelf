import Foundation

enum ClipKindFilter: String, CaseIterable, Identifiable {
    case all
    case text
    case file
    case image

    var id: Self { self }

    var title: String {
        switch self {
        case .all: "全部"
        case .text: "文字"
        case .file: "文件"
        case .image: "图片"
        }
    }

    func matches(_ item: ClipItem) -> Bool {
        switch self {
        case .all: true
        case .text: item.kind == .text
        case .file: item.kind == .file
        case .image: item.kind == .image
        }
    }
}

enum ClipHistoryFilter {
    static func items(_ items: [ClipItem], kind: ClipKindFilter, query: String) -> [ClipItem] {
        let trimmedQuery = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return items.filter { item in
            kind.matches(item) && SearchMatcher.matches(item, query: trimmedQuery)
        }
    }
}
