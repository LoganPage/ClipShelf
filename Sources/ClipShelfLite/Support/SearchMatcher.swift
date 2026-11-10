import Darwin
import Foundation

enum SearchMatcher {
    static func matches(_ item: ClipItem, query rawQuery: String) -> Bool {
        matches(ClipSearchIndex(item: item), query: rawQuery)
    }

    static func matches(_ index: ClipSearchIndex, query rawQuery: String) -> Bool {
        let query = normalize(rawQuery)
        return matchesNormalized(index, query: query)
    }

    static func matchesNormalized(_ index: ClipSearchIndex, query: String) -> Bool {
        guard !query.isEmpty else { return true }

        return index.fields.contains { field in
            fastContains(field.normalized, query)
                || fastContains(field.pinyin, query)
                || fastContains(field.initials, query)
                || isSubsequence(query, of: field.normalized)
                || isSubsequence(query, of: field.pinyin)
                || isSubsequence(query, of: field.initials)
                || fuzzyContains(query, in: field.normalized)
                || fuzzyContains(query, in: field.pinyin)
        }
    }

    static func matchesUnindexed(_ item: ClipItem, query rawQuery: String) -> Bool {
        let query = normalize(rawQuery)
        guard !query.isEmpty else { return true }

        return searchableTexts(for: item).contains { text in
            let transliterated = pinyinText(text)
            let normalized = normalize(text)
            let pinyin = normalize(transliterated)
            let initials = pinyinInitials(transliterated)

            return fastContains(normalized, query)
                || fastContains(pinyin, query)
                || fastContains(initials, query)
                || isSubsequence(query, of: normalized)
                || isSubsequence(query, of: pinyin)
                || isSubsequence(query, of: initials)
                || fuzzyContains(query, in: normalized)
                || fuzzyContains(query, in: pinyin)
        }
    }

    static func searchableTexts(for item: ClipItem) -> [String] {
        var values = [item.title]
        // A text record's title is its text: indexing both doubled the work.
        if let text = item.text, text != item.title {
            values.append(text)
        }
        values.append(contentsOf: item.filePaths)
        if let sourcePath = item.sourcePath {
            values.append(sourcePath)
        }
        return values
    }

    static func normalize(_ value: String) -> String {
        value
            .folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: .current)
            .lowercased()
            .filter { $0.isLetter || $0.isNumber }
    }

    static func pinyinText(_ value: String) -> String {
        let mutable = NSMutableString(string: value) as CFMutableString
        CFStringTransform(mutable, nil, kCFStringTransformToLatin, false)
        CFStringTransform(mutable, nil, kCFStringTransformStripDiacritics, false)
        return mutable as String
    }

    static func pinyinInitials(_ transliteratedPinyin: String) -> String {
        transliteratedPinyin
            .split { !$0.isLetter && !$0.isNumber }
            .compactMap(\.first)
            .map { String($0).lowercased() }
            .joined()
    }

    /// Substring test on raw UTF-8 bytes. UTF-8 is self-synchronising, so a byte
    /// match is a scalar match; search text is already normalised, so this agrees
    /// with Character-level `contains` while being many times faster.
    static func fastContains(_ haystack: String, _ needle: String) -> Bool {
        var haystack = haystack
        var needle = needle
        return haystack.withUTF8 { haystackBytes in
            needle.withUTF8 { needleBytes in
                guard needleBytes.count > 0 else { return true }
                guard haystackBytes.count >= needleBytes.count,
                      let haystackBase = haystackBytes.baseAddress,
                      let needleBase = needleBytes.baseAddress else { return false }
                return memmem(haystackBase, haystackBytes.count, needleBase, needleBytes.count) != nil
            }
        }
    }

    private static func isSubsequence(_ needle: String, of haystack: String) -> Bool {
        guard !needle.isEmpty else { return true }
        var iterator = haystack.unicodeScalars.makeIterator()

        for character in needle.unicodeScalars {
            var found = false
            while let next = iterator.next() {
                if next == character {
                    found = true
                    break
                }
            }
            if !found {
                return false
            }
        }

        return true
    }

    /// Fuzzy matching only looks at the start of very long clips so typing
    /// stays responsive; exact and subsequence matches still cover the rest.
    static let fuzzyScanLimit = 4_000

    static func fuzzyContains(_ query: String, in text: String) -> Bool {
        // Unicode scalars are far cheaper than Characters, and normalized search
        // text is letters and digits only, so the edit distance is unchanged.
        let queryScalars = Array(query.unicodeScalars)
        guard queryScalars.count >= 3 else { return false }
        if fastContains(text, query) { return true }

        let textScalars = text.unicodeScalars.prefix(fuzzyScanLimit)
        guard textScalars.count >= queryScalars.count else { return false }

        let distanceLimit = queryScalars.count <= 5 ? 1 : 2
        var previous = Array(0...queryScalars.count)
        var current = Array(repeating: 0, count: queryScalars.count + 1)

        for textScalar in textScalars {
            current[0] = 0
            for queryIndex in 1...queryScalars.count {
                let cost = queryScalars[queryIndex - 1] == textScalar ? 0 : 1
                current[queryIndex] = min(
                    previous[queryIndex] + 1,
                    current[queryIndex - 1] + 1,
                    previous[queryIndex - 1] + cost
                )
            }
            if current[queryScalars.count] <= distanceLimit {
                return true
            }
            swap(&previous, &current)
        }

        return false
    }
}
