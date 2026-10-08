import CoreFoundation
import Foundation

enum ProtocolValidation {
    private static let uuidRegex = try! NSRegularExpression(
        pattern: "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"
    )
    private static let timestampRegex = try! NSRegularExpression(
        pattern: "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z$"
    )
    private static let capabilityRegex = try! NSRegularExpression(
        pattern: "^[a-z][a-z0-9._-]{0,31}$"
    )
    private static let contentHashRegex = try! NSRegularExpression(
        pattern: "^sha256:[0-9a-f]{64}$"
    )

    static func isCanonicalNonzeroUUID(_ value: String) -> Bool {
        matches(uuidRegex, value) && value != "00000000-0000-0000-0000-000000000000"
            && UUID(uuidString: value) != nil
    }

    static func isValidTimestamp(_ value: String) -> Bool {
        guard matches(timestampRegex, value) else { return false }
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm:ss.SSS'Z'"
        formatter.isLenient = false
        guard let date = formatter.date(from: value) else { return false }
        return formatter.string(from: date) == value
    }

    static func isValidCapability(_ value: String) -> Bool {
        matches(capabilityRegex, value)
    }

    static func isValidContentHash(_ value: String) -> Bool {
        matches(contentHashRegex, value)
    }

    static func scalarCount(_ value: String) -> Int {
        value.unicodeScalars.count
    }

    static func isAllWhitespace(_ value: String) -> Bool {
        !value.isEmpty && value.unicodeScalars.allSatisfy { $0.properties.isWhitespace }
    }

    static func isBoolean(_ value: Any) -> Bool {
        guard let number = value as? NSNumber else { return false }
        return CFGetTypeID(number) == CFBooleanGetTypeID()
    }

    static func uint64(_ value: Any) -> UInt64? {
        guard let number = value as? NSNumber, !isBoolean(value) else { return nil }
        let decimal = number.decimalValue
        var source = decimal
        var rounded = Decimal()
        NSDecimalRound(&rounded, &source, 0, .plain)
        guard rounded == decimal, rounded >= 0 else { return nil }
        return UInt64(NSDecimalNumber(decimal: rounded).stringValue)
    }

    static func positiveInt(_ value: Any) -> Int? {
        guard let unsigned = uint64(value), unsigned >= 1, unsigned <= UInt64(Int.max) else { return nil }
        return Int(unsigned)
    }

    private static func matches(_ regex: NSRegularExpression, _ value: String) -> Bool {
        let range = NSRange(value.startIndex..<value.endIndex, in: value)
        return regex.firstMatch(in: value, range: range)?.range == range
    }
}
