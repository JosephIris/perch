// Claude's answers are markdown. Read aloud, the markup is noise and a code
// block is gibberish, so this turns a reply into what a person would say.

import Foundation

enum SpokenText {
    static func from(markdown: String) -> String {
        var lines: [String] = []
        var inFence = false
        for raw in markdown.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("```") || line.hasPrefix("~~~") {
                if !inFence { lines.append("Code block skipped.") }
                inFence.toggle()
                continue
            }
            if inFence { continue }
            // A table's divider row says nothing.
            if line.hasPrefix("|") && line.allSatisfy({ "|-: ".contains($0) }) { continue }
            lines.append(inline(line))
        }
        return lines
            .joined(separator: "\n")
            .replacingOccurrences(of: "\n{3,}", with: "\n\n", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func inline(_ line: String) -> String {
        var s = line
        let rules: [(String, String)] = [
            (#"^#{1,6}\s+"#, ""),                    // headings
            (#"^>\s?"#, ""),                          // quotes
            (#"^[-*+]\s+"#, ""),                      // bullets
            (#"^\[[ xX]\]\s+"#, ""),                  // task boxes
            (#"!\[([^\]]*)\]\([^)]*\)"#, "$1"),       // images
            (#"\[([^\]]+)\]\([^)]*\)"#, "$1"),        // links
            (#"https?://\S+"#, "a link"),            // bare urls
            (#"`([^`]*)`"#, "$1"),                    // inline code
            (#"(\*\*|__)(.+?)\1"#, "$2"),             // bold
            (#"(?<![\w*])[*_](?!\s)(.+?)(?<!\s)[*_](?![\w*])"#, "$1"), // italics
            (#"~~(.+?)~~"#, "$1"),                    // strikethrough
            (#"\s*\|\s*"#, ", "),                     // table cells
        ]
        for (pattern, template) in rules {
            s = s.replacingOccurrences(of: pattern, with: template, options: .regularExpression)
        }
        return s.trimmingCharacters(in: CharacterSet(charactersIn: ", ").union(.whitespaces))
    }
}
