// A permission prompt, answered from the phone: what Claude wants to do, shown
// the way you'd want to judge it (the command in a terminal box, an edit as a
// red/green diff, the page it wants to fetch), and the prompt's own choices:
// allow, always allow (when the prompt offers it), deny, or deny and tell
// Claude what to do instead. The same prompt stays up in the terminal on the
// computer; whichever answers first wins.

import SwiftUI

struct ApprovalCard: View {
    let id: UUID
    let ask: PermissionAsk?
    @Environment(AppModel.self) private var model
    @State private var note = ""
    @State private var noting = false
    @State private var busy = false
    @State private var error: String?
    @State private var expanded = false
    @FocusState private var noteFocused: Bool

    private var tool: String { ask?.tool ?? "" }
    private var input: [String: Any] {
        guard let s = ask?.input, let d = s.data(using: .utf8),
              let o = try? JSONSerialization.jsonObject(with: d) as? [String: Any] else { return [:] }
        return o
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Image(systemName: icon)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(width: 26, height: 26)
                    .background(Color.red.gradient, in: RoundedRectangle(cornerRadius: 7, style: .continuous))
                VStack(alignment: .leading, spacing: 0) {
                    Text(title).font(.subheadline.weight(.semibold))
                    Text("Claude is waiting for your answer").font(.caption2).foregroundStyle(.secondary)
                }
                Spacer()
            }

            detail
                .frame(maxHeight: expanded ? 360 : 150, alignment: .top)
                .fixedSize(horizontal: false, vertical: true)
                .overlay(alignment: .bottomTrailing) {
                    if !expanded && needsExpand {
                        Button("More") { withAnimation(.easeOut(duration: 0.15)) { expanded = true } }
                            .font(.caption.weight(.semibold))
                            .padding(.horizontal, 8).padding(.vertical, 3)
                            .background(.thinMaterial, in: Capsule())
                            .padding(6)
                    }
                }

            if let error {
                Text(error).font(.caption).foregroundStyle(.orange)
            }

            if noting {
                HStack(alignment: .bottom, spacing: 8) {
                    TextField("Tell Claude what to do instead", text: $note, axis: .vertical)
                        .lineLimit(1...4)
                        .focused($noteFocused)
                        .padding(.horizontal, 10).padding(.vertical, 7)
                        .background(Color(.tertiarySystemFill), in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                    Button {
                        respond("deny", text: note)
                    } label: {
                        Image(systemName: "arrow.up.circle.fill").font(.system(size: 30))
                    }
                    .disabled(note.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || busy)
                    .accessibilityLabel("Deny and send")
                }
            }

            HStack(spacing: 8) {
                Button(role: .destructive) { respond("deny") } label: {
                    Text("Deny").frame(maxWidth: .infinity)
                }
                .buttonStyle(.bordered)
                .accessibilityIdentifier("denyButton")
                Button { respond("allow") } label: {
                    Text("Allow").fontWeight(.semibold).frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .tint(.green)
                .accessibilityIdentifier("allowButton")
            }
            .controlSize(.large)
            .disabled(busy)

            HStack {
                if ask?.canAlways == true {
                    Button { respond("always") } label: {
                        Label(alwaysLabel, systemImage: "checkmark.seal")
                            .lineLimit(1)
                    }
                }
                Spacer(minLength: 8)
                if !noting {
                    Button("Deny with a note") {
                        noting = true
                        noteFocused = true
                    }
                }
            }
            .font(.caption)
            .disabled(busy)
        }
        .padding(12)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(Color.red.opacity(0.35), lineWidth: 1))
        .sensoryFeedback(.warning, trigger: id)
    }

    private func respond(_ answer: String, text: String? = nil) {
        busy = true
        error = nil
        let words = text?.trimmingCharacters(in: .whitespacesAndNewlines)
        Task {
            error = await model.answer(id, answer, text: (words?.isEmpty ?? true) ? nil : words)
            busy = false
        }
    }

    // MARK: - what it asks

    private var icon: String {
        switch tool {
        case "Bash", "PowerShell": return "terminal"
        case "Edit", "MultiEdit", "NotebookEdit": return "pencil"
        case "Write": return "doc.badge.plus"
        case "Read": return "doc.text"
        case "WebFetch", "WebSearch": return "globe"
        case "": return "hand.raised.fill"
        default: return tool.hasPrefix("mcp__") ? "puzzlepiece.extension" : "wrench.and.screwdriver"
        }
    }

    private var title: String {
        switch tool {
        case "Bash", "PowerShell": return "Run a command?"
        case "Edit", "MultiEdit", "NotebookEdit": return "Edit \(fileName)?"
        case "Write": return "Write \(fileName)?"
        case "Read": return "Read \(fileName)?"
        case "WebFetch": return "Open a web page?"
        case "WebSearch": return "Search the web?"
        case "": return "Claude wants your approval"
        default:
            let name = tool.hasPrefix("mcp__") ? tool.split(separator: "__").dropFirst().joined(separator: " · ") : tool
            return "Use \(name)?"
        }
    }

    private var fileName: String {
        let path = (input["file_path"] as? String) ?? (input["notebook_path"] as? String) ?? ask?.summary ?? "a file"
        return (path as NSString).lastPathComponent
    }

    private var alwaysLabel: String {
        if let rule = ask?.rules?.first, !rule.isEmpty { return "Always allow \(rule)" }
        return "Yes, don't ask again"
    }

    private var needsExpand: Bool {
        switch tool {
        case "Bash", "PowerShell": return ((input["command"] as? String) ?? "").count > 160
        case "Edit", "MultiEdit", "Write": return true
        default: return (ask?.input?.count ?? 0) > 200
        }
    }

    @ViewBuilder
    private var detail: some View {
        switch tool {
        case "Bash", "PowerShell":
            VStack(alignment: .leading, spacing: 6) {
                CodeBox(text: "$ " + ((input["command"] as? String) ?? ask?.summary ?? ""), dark: true)
                if let why = input["description"] as? String, !why.isEmpty {
                    Text(why).font(.caption).foregroundStyle(.secondary)
                }
            }
        case "Edit":
            VStack(alignment: .leading, spacing: 6) {
                PathLine(path: (input["file_path"] as? String) ?? ask?.summary ?? "")
                DiffBox(old: input["old_string"] as? String ?? "", new: input["new_string"] as? String ?? "")
            }
        case "MultiEdit":
            VStack(alignment: .leading, spacing: 6) {
                PathLine(path: (input["file_path"] as? String) ?? ask?.summary ?? "")
                let edits = (input["edits"] as? [[String: Any]]) ?? []
                ScrollView {
                    VStack(alignment: .leading, spacing: 6) {
                        ForEach(Array(edits.enumerated()), id: \.offset) { _, e in
                            DiffBox(old: e["old_string"] as? String ?? "", new: e["new_string"] as? String ?? "", scrolls: false)
                        }
                    }
                }
            }
        case "Write":
            VStack(alignment: .leading, spacing: 6) {
                PathLine(path: (input["file_path"] as? String) ?? ask?.summary ?? "")
                DiffBox(old: "", new: input["content"] as? String ?? "")
            }
        case "WebFetch":
            VStack(alignment: .leading, spacing: 4) {
                Text((input["url"] as? String) ?? ask?.summary ?? "").font(.footnote.monospaced()).lineLimit(3)
                if let p = input["prompt"] as? String { Text(p).font(.caption).foregroundStyle(.secondary).lineLimit(3) }
            }
        case "" where ask == nil:
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("Getting what it wants to do…").font(.footnote).foregroundStyle(.secondary)
            }
        case "":
            Text("The details didn't reach the phone. You can still answer, or look at the terminal on the computer.")
                .font(.footnote).foregroundStyle(.secondary)
        default:
            VStack(alignment: .leading, spacing: 4) {
                if let s = ask?.summary, !s.isEmpty, s != tool { Text(s).font(.footnote) }
                if let raw = ask?.input { CodeBox(text: Self.pretty(raw), dark: false) }
            }
        }
    }

    static func pretty(_ raw: String) -> String {
        guard let d = raw.data(using: .utf8), let o = try? JSONSerialization.jsonObject(with: d),
              let p = try? JSONSerialization.data(withJSONObject: o, options: [.prettyPrinted, .sortedKeys]),
              let s = String(data: p, encoding: .utf8) else { return raw }
        return s
    }
}

private struct PathLine: View {
    let path: String
    var body: some View {
        Text(path)
            .font(.caption.monospaced())
            .foregroundStyle(.secondary)
            .lineLimit(2)
            .truncationMode(.head)
    }
}

/// Text in a box: a terminal (dark) for commands, a plain box otherwise.
struct CodeBox: View {
    let text: String
    let dark: Bool
    var body: some View {
        ScrollView([.vertical, .horizontal], showsIndicators: false) {
            Text(text)
                .font(.footnote.monospaced())
                .foregroundStyle(dark ? Color(hex: 0xE8EEF7) : .primary)
                .textSelection(.enabled)
                .padding(10)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .fixedSize(horizontal: false, vertical: true)
        .background(dark ? Color(hex: 0x15171C) : Color(.tertiarySystemFill),
                    in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}

/// An edit as lines removed (red) and added (green).
struct DiffBox: View {
    let old: String
    let new: String
    var scrolls = true

    private var lines: [(String, Bool?)] {
        let o = old.isEmpty ? [] : old.components(separatedBy: "\n")
        let n = new.isEmpty ? [] : new.components(separatedBy: "\n")
        // Lines both sides share at the start and end stay plain.
        var head = 0
        while head < o.count, head < n.count, o[head] == n[head] { head += 1 }
        var tail = 0
        while tail < o.count - head, tail < n.count - head, o[o.count - 1 - tail] == n[n.count - 1 - tail] { tail += 1 }
        var out: [(String, Bool?)] = o[..<head].map { ($0, nil) }
        out += o[head..<(o.count - tail)].map { ($0, false) }
        out += n[head..<(n.count - tail)].map { ($0, true) }
        out += n[(n.count - tail)...].map { ($0, nil) }
        return out
    }

    var body: some View {
        let content = VStack(alignment: .leading, spacing: 0) {
            ForEach(Array(lines.enumerated()), id: \.offset) { _, line in
                HStack(alignment: .top, spacing: 6) {
                    Text(line.1 == nil ? " " : line.1! ? "+" : "−")
                        .foregroundStyle(line.1 == nil ? Color.secondary : line.1! ? Color.green : Color.red)
                    Text(line.0.isEmpty ? " " : line.0)
                }
                .font(.caption.monospaced())
                .padding(.horizontal, 8)
                .padding(.vertical, 1)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(line.1 == nil ? Color.clear : line.1! ? Color.green.opacity(0.14) : Color.red.opacity(0.14))
            }
        }
        .padding(.vertical, 6)
        Group {
            if scrolls {
                ScrollView([.vertical, .horizontal], showsIndicators: false) { content }
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                content
            }
        }
        .background(Color(.tertiarySystemFill), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }
}
