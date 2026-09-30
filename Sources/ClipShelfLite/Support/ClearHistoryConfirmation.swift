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
    case paste
    case pin
    case delete
}

enum ClipRowMenu {
    static let orderedActions: [ClipRowMenuAction] = [.copy, .paste, .pin, .delete]

    static func title(for action: ClipRowMenuAction) -> String {
        switch action {
        case .copy:
            return "复制"
        case .paste:
            return "粘贴"
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
        guard let menuBlock = contextMenuBlock(in: source) else {
            return false
        }

        return sourceContains(#"ForEach\(\s*ClipRowMenu\.orderedActions"#, in: menuBlock)
            && sourceContains(#"\brole:\s*\.destructive"#, in: menuBlock)
    }

    private static func sourceContains(_ pattern: String, in source: String) -> Bool {
        source.range(of: pattern, options: .regularExpression) != nil
    }

    private static func contextMenuBlock(in source: String) -> String? {
        guard let menuStart = source.range(
            of: #"\.contextMenu\s*\{"#,
            options: .regularExpression
        ), let openingBrace = source[menuStart].lastIndex(of: "{") else {
            return nil
        }

        var depth = 0
        var index = openingBrace
        while index < source.endIndex {
            switch source[index] {
            case "{":
                depth += 1
            case "}":
                depth -= 1
                if depth == 0 {
                    return String(source[menuStart.lowerBound...index])
                }
            default:
                break
            }
            index = source.index(after: index)
        }

        return nil
    }
}
