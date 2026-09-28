import Foundation

enum SelectionCountLabel {
    static func text(for count: Int) -> String? {
        guard count > 0 else { return nil }
        return "已选 \(count) 条"
    }
}
