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
            field.normalized.contains(query)
                || field.pinyin.contains(query)
                || field.initials.contains(query)
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

            return normalized.contains(query)
                || pinyin.contains(query)
                || initials.contains(query)
                || isSubsequence(query, of: normalized)
                || isSubsequence(query, of: pinyin)
                || isSubsequence(query, of: initials)
                || fuzzyContains(query, in: normalized)
                || fuzzyContains(query, in: pinyin)
        }
    }

    static func searchableTexts(for item: ClipItem) -> [String] {
        var values = [item.title]
        if let text = item.text {
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

    private static func isSubsequence(_ needle: String, of haystack: String) -> Bool {
        guard !needle.isEmpty else { return true }
        var iterator = haystack.makeIterator()

        for character in needle {
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

    static func fuzzyContains(_ query: String, in text: String) -> Bool {
        guard query.count >= 3, text.count >= query.count else { return false }
        if text.contains(query) { return true }

        let queryCharacters = Array(query)
        let textCharacters = Array(text)
        let distanceLimit = queryCharacters.count <= 5 ? 1 : 2
        var previous = Array(0...queryCharacters.count)
        var current = Array(repeating: 0, count: queryCharacters.count + 1)

        for textCharacter in textCharacters {
            current[0] = 0
            for queryIndex in 1...queryCharacters.count {
                let cost = queryCharacters[queryIndex - 1] == textCharacter ? 0 : 1
                current[queryIndex] = min(
                    previous[queryIndex] + 1,
                    current[queryIndex - 1] + 1,
                    previous[queryIndex - 1] + cost
                )
            }
            if current[queryCharacters.count] <= distanceLimit {
                return true
            }
            swap(&previous, &current)
        }

        return false
    }
}
