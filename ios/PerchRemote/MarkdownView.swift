// Claude's answers are markdown. This draws the parts it actually writes:
// headings, paragraphs, bullet and numbered lists (nested), quotes, tables,
// rules, and fenced code (with its language and a copy button), with inline
// bold, italics, links and `code` inside them.

import SwiftUI

struct MarkdownText: View {
    let text: String

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            ForEach(Array(Markdown.blocks(text).enumerated()), id: \.offset) { _, block in
                BlockView(block: block)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

enum Markdown {
    enum Block {
        case heading(Int, String)
        case paragraph(String)
        case list([Item])
        case quote(String)
        case code(lang: String, text: String)
        case table(header: [String], rows: [[String]])
        case rule
    }

    struct Item {
        let depth: Int
        let marker: String     // "•", "◦", "1." …
        let text: String
    }

    static func blocks(_ text: String) -> [Block] {
        var out: [Block] = []
        var para: [String] = []
        var items: [Item] = []
        var quote: [String] = []
        let lines = text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")

        func flush() {
            if !para.isEmpty { out.append(.paragraph(para.joined(separator: "\n"))); para = [] }
            if !items.isEmpty { out.append(.list(items)); items = [] }
            if !quote.isEmpty { out.append(.quote(quote.joined(separator: "\n"))); quote = [] }
        }

        var i = 0
        while i < lines.count {
            let line = lines[i]
            let trimmed = line.trimmingCharacters(in: .whitespaces)

            // Fenced code: everything up to the closing fence, verbatim.
            if trimmed.hasPrefix("```") || trimmed.hasPrefix("~~~") {
                flush()
                let fence = String(trimmed.prefix(3))
                let lang = String(trimmed.dropFirst(3)).trimmingCharacters(in: .whitespaces)
                var code: [String] = []
                i += 1
                while i < lines.count, !lines[i].trimmingCharacters(in: .whitespaces).hasPrefix(fence) {
                    code.append(lines[i]); i += 1
                }
                out.append(.code(lang: lang, text: code.joined(separator: "\n")))
                i += 1
                continue
            }

            if trimmed.isEmpty { flush(); i += 1; continue }

            // A table: a row of cells, then a |---|---| divider.
            if trimmed.hasPrefix("|"), i + 1 < lines.count, isDivider(lines[i + 1]) {
                flush()
                let header = cells(trimmed)
                var rows: [[String]] = []
                i += 2
                while i < lines.count, lines[i].trimmingCharacters(in: .whitespaces).hasPrefix("|") {
                    rows.append(cells(lines[i].trimmingCharacters(in: .whitespaces))); i += 1
                }
                out.append(.table(header: header, rows: rows))
                continue
            }

            if let r = trimmed.range(of: #"^#{1,6}\s+"#, options: .regularExpression) {
                flush()
                let level = trimmed[r].filter { $0 == "#" }.count
                out.append(.heading(level, String(trimmed[r.upperBound...])))
                i += 1; continue
            }

            if trimmed.range(of: #"^([-*_])(\s*\1){2,}$"#, options: .regularExpression) != nil {
                flush(); out.append(.rule); i += 1; continue
            }

            if trimmed.hasPrefix(">") {
                if !para.isEmpty || !items.isEmpty { let q = quote; flush(); quote = q }
                quote.append(String(trimmed.dropFirst()).trimmingCharacters(in: .whitespaces))
                i += 1; continue
            }

            // List items, nested by indentation (2 spaces or a tab a level).
            let indent = line.prefix { $0 == " " || $0 == "\t" }.reduce(0) { $0 + ($1 == "\t" ? 4 : 1) }
            if let r = trimmed.range(of: #"^([-*+]|\d{1,3}[.)])\s+"#, options: .regularExpression) {
                if !para.isEmpty || !quote.isEmpty { let its = items; flush(); items = its }
                let raw = trimmed[r].trimmingCharacters(in: .whitespaces)
                let depth = min(indent / 2, 4)
                let marker = raw.first!.isNumber ? raw.replacingOccurrences(of: ")", with: ".")
                    : depth == 0 ? "•" : "◦"
                items.append(Item(depth: depth, marker: marker, text: String(trimmed[r.upperBound...])))
                i += 1; continue
            }

            // A wrapped line of the list item above it.
            if !items.isEmpty, indent >= 2, let last = items.popLast() {
                items.append(Item(depth: last.depth, marker: last.marker, text: last.text + " " + trimmed))
                i += 1; continue
            }

            if !items.isEmpty || !quote.isEmpty { flush() }
            para.append(trimmed)
            i += 1
        }
        flush()
        return out
    }

    private static func isDivider(_ line: String) -> Bool {
        let t = line.trimmingCharacters(in: .whitespaces)
        return t.hasPrefix("|") && t.contains("-") && t.allSatisfy { "|-: ".contains($0) }
    }

    private static func cells(_ row: String) -> [String] {
        var t = row
        if t.hasPrefix("|") { t.removeFirst() }
        if t.hasSuffix("|") { t.removeLast() }
        return t.components(separatedBy: "|").map { $0.trimmingCharacters(in: .whitespaces) }
    }

    /// Inline markdown (bold, italics, links, `code`), with code tinted.
    static func inline(_ s: String) -> AttributedString {
        var a = (try? AttributedString(markdown: s, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(s)
        for run in a.runs where run.inlinePresentationIntent?.contains(.code) == true {
            a[run.range].font = .system(.callout, design: .monospaced)
            a[run.range].backgroundColor = Color(.tertiarySystemFill)
        }
        return a
    }
}

private struct BlockView: View {
    let block: Markdown.Block

    var body: some View {
        switch block {
        case .heading(let level, let text):
            Text(Markdown.inline(text))
                .font(level <= 1 ? .title3.bold() : level == 2 ? .headline : .subheadline.weight(.semibold))
                .padding(.top, 2)
        case .paragraph(let text):
            Text(Markdown.inline(text)).font(.callout).textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
        case .list(let items):
            VStack(alignment: .leading, spacing: 3) {
                ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(item.marker)
                            .font(.callout.monospacedDigit())
                            .foregroundStyle(.secondary)
                            .frame(minWidth: 12, alignment: .trailing)
                        Text(Markdown.inline(item.text)).font(.callout)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .padding(.leading, CGFloat(item.depth) * 16)
                }
            }
            .textSelection(.enabled)
        case .quote(let text):
            HStack(spacing: 8) {
                RoundedRectangle(cornerRadius: 1.5).fill(Color.secondary.opacity(0.4)).frame(width: 3)
                Text(Markdown.inline(text)).font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        case .code(let lang, let text):
            CodeBlock(lang: lang, text: text)
        case .table(let header, let rows):
            ScrollView(.horizontal, showsIndicators: false) {
                Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 6) {
                    GridRow {
                        ForEach(Array(header.enumerated()), id: \.offset) { _, c in
                            Text(Markdown.inline(c)).font(.caption.weight(.semibold))
                        }
                    }
                    Divider()
                    ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                        GridRow {
                            ForEach(Array(row.enumerated()), id: \.offset) { _, c in
                                Text(Markdown.inline(c)).font(.caption)
                            }
                        }
                    }
                }
                .padding(10)
            }
            .background(Color(.tertiarySystemFill).opacity(0.5), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
        case .rule:
            Divider().padding(.vertical, 2)
        }
    }
}

/// A fenced code block: dark, monospaced, scrolling sideways rather than
/// wrapping, with its language and a copy button.
struct CodeBlock: View {
    let lang: String
    let text: String
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(lang.isEmpty ? "code" : lang)
                    .font(.caption2.monospaced())
                    .foregroundStyle(Color(hex: 0xE8EEF7).opacity(0.55))
                Spacer()
                Button {
                    UIPasteboard.general.string = text
                    copied = true
                    Task { try? await Task.sleep(for: .seconds(1.5)); copied = false }
                } label: {
                    Label(copied ? "Copied" : "Copy", systemImage: copied ? "checkmark" : "doc.on.doc")
                        .font(.caption2)
                }
                .buttonStyle(.plain)
                .foregroundStyle(Color(hex: 0xE8EEF7).opacity(0.7))
            }
            .padding(.horizontal, 10)
            .padding(.top, 7)
            ScrollView(.horizontal, showsIndicators: false) {
                Text(text)
                    .font(.system(size: 12.5, design: .monospaced))
                    .foregroundStyle(Color(hex: 0xE8EEF7))
                    .textSelection(.enabled)
                    .padding(10)
            }
        }
        .background(Color(hex: 0x15171C), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}
