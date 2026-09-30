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
        source.contains(".contextMenu {")
            && source.contains("ForEach(ClipRowMenu.orderedActions")
    }
}
