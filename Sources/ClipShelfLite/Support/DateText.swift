import Foundation

enum DateText {
    static let formatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateStyle = .none
        formatter.timeStyle = .medium
        return formatter
    }()

    private static func makeFormatter(_ format: String) -> DateFormatter {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = format
        return formatter
    }

    private static let timeOnly = makeFormatter("HH:mm")
    private static let monthDayTime = makeFormatter("M月d日 HH:mm")
    private static let fullDate = makeFormatter("yyyy年M月d日")

    /// How a record's time reads in the list: "今天 14:05", "昨天 23:50",
    /// "10月25日 09:12", or "2025年3月2日" for earlier years.
    static func relative(_ date: Date, now: Date = Date(), calendar: Calendar = .current) -> String {
        if calendar.isDate(date, inSameDayAs: now) {
            return "今天 " + timeOnly.string(from: date)
        }
        if let yesterday = calendar.date(byAdding: .day, value: -1, to: now),
           calendar.isDate(date, inSameDayAs: yesterday) {
            return "昨天 " + timeOnly.string(from: date)
        }
        if calendar.component(.year, from: date) == calendar.component(.year, from: now) {
            return monthDayTime.string(from: date)
        }
        return fullDate.string(from: date)
    }
}
