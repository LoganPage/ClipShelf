import Foundation

enum ClearHistoryConfirmation {
    static func shouldConfirm(itemCount: Int) -> Bool {
        itemCount > 0
    }

    static func title(itemCount: Int) -> String {
        itemCount == 1 ? "清空这条历史记录？" : "清空全部历史记录？"
    }

    static func message(itemCount: Int) -> String {
        "将清空 \(max(itemCount, 0)) 条记录。清空后仍可使用 Command-Z 撤销。"
    }
}

enum ClipRowMenuAction: CaseIterable {
    case copy
    case pin
    case delete
}

enum ClipRowMenu {
    static let orderedActions: [ClipRowMenuAction] = [.copy, .pin, .delete]

    static func title(for action: ClipRowMenuAction) -> String {
        switch action {
        case .copy:
            return "复制"
        case .pin:
            return pinTitle(isPinned: false)
        case .delete:
            return "删除"
        }
    }

    static func pinTitle(isPinned: Bool) -> String {
        isPinned ? "取消置顶" : "置顶"
    }

    static func isDeclaredMenuInstalled(in source: String) -> Bool {
        let code = SourceScan.codeOnly(source)
        guard let clipRowBody = SourceScan.body(ofTypeNamed: "ClipRow", in: code) else {
            return false
        }

        let menuBlocks = contextMenuBlocks(in: clipRowBody)
        guard menuBlocks.count == 1, let menuBlock = menuBlocks.first else {
            return false
        }

        return SourceScan.contains(#"ForEach\(\s*ClipRowMenu\.orderedActions"#, in: menuBlock)
            && SourceScan.contains(
                #"case\s+\.delete:(?:(?!\n\s*case\s+\.)[\s\S])*?\brole:\s*\.destructive"#,
                in: menuBlock
            )
    }

    private static func contextMenuBlocks(in source: String) -> [String] {
        var blocks = [String]()
        var searchStart = source.startIndex

        while searchStart < source.endIndex,
              let menuStart = source.range(
                of: #"\.contextMenu\s*\{"#,
                options: .regularExpression,
                range: searchStart..<source.endIndex
              ),
              let openingBrace = source[menuStart].lastIndex(of: "{") {
            var depth = 0
            var index = openingBrace
            var closingBrace: String.Index?

            while index < source.endIndex {
                switch source[index] {
                case "{":
                    depth += 1
                case "}":
                    depth -= 1
                    if depth == 0 {
                        closingBrace = index
                    }
                default:
                    break
                }
                if closingBrace != nil {
                    break
                }
                index = source.index(after: index)
            }

            guard let closingBrace else {
                return []
            }
            blocks.append(String(source[menuStart.lowerBound...closingBrace]))
            searchStart = source.index(after: closingBrace)
        }

        return blocks
    }
}
