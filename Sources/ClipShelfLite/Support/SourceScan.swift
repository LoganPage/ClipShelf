import Foundation

/// Source-only checks used by `--self-test`.
enum SourceScan {
    static func codeOnly(_ source: String) -> String {
        var result = ""
        var index = source.startIndex

        while index < source.endIndex {
            let character = source[index]
            let nextIndex = source.index(after: index)
            let nextCharacter = nextIndex < source.endIndex ? source[nextIndex] : nil

            if character == "\"" {
                result.append("\"\"")
                index = nextIndex
                while index < source.endIndex {
                    let stringCharacter = source[index]
                    if stringCharacter == "\n" {
                        result.append("\n")
                    }
                    if stringCharacter == "\\" {
                        index = source.index(after: index)
                        if index < source.endIndex {
                            index = source.index(after: index)
                        }
                        continue
                    }
                    index = source.index(after: index)
                    if stringCharacter == "\"" {
                        break
                    }
                }
                continue
            }

            if character == "/", nextCharacter == "/" {
                index = source.index(after: nextIndex)
                while index < source.endIndex, source[index] != "\n" {
                    index = source.index(after: index)
                }
                continue
            }

            if character == "/", nextCharacter == "*" {
                var depth = 1
                index = source.index(after: nextIndex)
                while index < source.endIndex, depth > 0 {
                    let commentCharacter = source[index]
                    let commentNextIndex = source.index(after: index)
                    let commentNextCharacter = commentNextIndex < source.endIndex
                        ? source[commentNextIndex]
                        : nil

                    if commentCharacter == "\n" {
                        result.append("\n")
                    }
                    if commentCharacter == "/", commentNextCharacter == "*" {
                        depth += 1
                        index = source.index(after: commentNextIndex)
                    } else if commentCharacter == "*", commentNextCharacter == "/" {
                        depth -= 1
                        index = source.index(after: commentNextIndex)
                    } else {
                        index = commentNextIndex
                    }
                }
                continue
            }

            result.append(character)
            index = nextIndex
        }

        return result
    }

    static func body(ofTypeNamed name: String, in source: String) -> String? {
        let escapedName = NSRegularExpression.escapedPattern(for: name)
        let pattern = #"\b(?:struct|class|enum|extension)\s+"# + escapedName + #"\b"#
        return body(after: pattern, in: codeOnly(source))
    }

    static func body(ofFunctionNamed name: String, in source: String) -> String? {
        let escapedName = NSRegularExpression.escapedPattern(for: name)
        let pattern = #"\bfunc\s+"# + escapedName + #"\b\s*\("#
        return body(after: pattern, in: codeOnly(source))
    }

    static func contains(_ pattern: String, in source: String) -> Bool {
        source.range(of: pattern, options: .regularExpression) != nil
    }

    private static func body(after pattern: String, in source: String) -> String? {
        guard let declaration = source.range(of: pattern, options: .regularExpression),
              let openingBrace = source[declaration.upperBound...].firstIndex(of: "{") else {
            return nil
        }
        return bracedBody(startingAt: openingBrace, in: source)
    }

    private static func bracedBody(startingAt openingBrace: String.Index, in source: String) -> String? {
        var depth = 0
        var index = openingBrace
        let bodyStart = source.index(after: openingBrace)

        while index < source.endIndex {
            switch source[index] {
            case "{":
                depth += 1
            case "}":
                depth -= 1
                if depth == 0 {
                    return String(source[bodyStart..<index])
                }
            default:
                break
            }
            index = source.index(after: index)
        }

        return nil
    }
}
