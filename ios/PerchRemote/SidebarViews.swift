// A computer's sessions drawn the way the desktop's sidebar draws them in
// projects mode (src/web/src/sidebar.ts): a header per project wearing the
// most urgent state of its tabs, its project chats pinned first with their
// threads under them, then its sessions; and each row in the same columns —
// the state dot (a spinner while it works), the agent's mark, the title, and
// at the right edge the turn's age, its running time, or "permission".

import SwiftUI

/// The desktop's state palette (tokens.css --color-state-*).
enum SidebarState {
    static func color(_ state: String) -> Color {
        switch state {
        case "working": return Color(hex: 0x4CC2FF)
        case "done": return Color(hex: 0x6CCB5F)
        case "waiting": return Color(UIColor { $0.userInterfaceStyle == .dark
            ? UIColor(red: 0xFC / 255, green: 0xE1 / 255, blue: 0, alpha: 1)
            : UIColor(red: 0xC9 / 255, green: 0xA2 / 255, blue: 0, alpha: 1) })   // the yellow, readable on white
        case "permission": return Color(hex: 0xFF5C6C)
        default: return .secondary
        }
    }

    /// The desktop's aggregateState: the most urgent wins.
    static func rank(_ state: String) -> Int {
        ["idle": 0, "working": 1, "done": 2, "waiting": 3, "permission": 4][state] ?? 0
    }

    static func aggregate(_ sessions: [PerchSession]) -> String {
        sessions.filter { !$0.asleep }.map(\.state).max { rank($0) < rank($1) } ?? "idle"
    }
}

/// The status column: a fixed 10 pt slot so every title starts at the same x.
struct StatusSlot: View {
    let state: String
    let asleep: Bool

    var body: some View {
        Group {
            if asleep {
                Circle().fill(Color.secondary.opacity(0.5)).frame(width: 8, height: 8)
            } else if state == "working" {
                BrailleSpinner().foregroundStyle(SidebarState.color("working"))
            } else if state == "idle" {
                Circle().strokeBorder(Color.secondary.opacity(0.6), lineWidth: 1).frame(width: 8, height: 8)
            } else {
                Circle().fill(SidebarState.color(state)).frame(width: 8, height: 8)
            }
        }
        .frame(width: 10)
    }
}

/// The braille spinner Claude Code draws in the pane, echoed in the row.
struct BrailleSpinner: View {
    private static let frames = Array("⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏")
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        TimelineView(.periodic(from: .now, by: 0.08)) { tl in
            let i = reduceMotion ? 0 : Int(tl.date.timeIntervalSinceReferenceDate / 0.08) % Self.frames.count
            Text(String(Self.frames[i])).font(.system(size: 12, weight: .bold, design: .monospaced))
        }
    }
}

/// The agent's own mark, from the same pixel sprites as the desktop
/// (agent-glyph.ts): Claude Code's creature, the Codex blob; a speech bubble
/// for a project chat.
struct AgentMark: View {
    let session: PerchSession

    private static let claude = ["...############...", "...##.######.##...", ".################.",
                                 "...############...", "....#.#....#.#...."]
    private static let codex = [".....####.......", "....#########...", "...###########..", "..#############.",
                                ".##############.", "####.##########.", "#####.#########.", "######.#########",
                                "######.#########", ".####.##########", ".###.###....####", ".##############.",
                                ".#############..", "..###########...", "...#########....", ".......####....."]

    var body: some View {
        switch session.kind {
        case "chat":
            Image(systemName: "bubble.left")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.secondary)
                .frame(width: 16, height: 12)
        case "codex":
            Sprite(rows: Self.codex, pixelHeight: 1).foregroundStyle(.secondary).frame(width: 12, height: 12)
        case "shell":
            Image(systemName: "terminal")
                .font(.system(size: 10, weight: .medium))
                .foregroundStyle(.tertiary)
                .frame(width: 16, height: 12)
        default:
            Sprite(rows: Self.claude, pixelHeight: 2).foregroundStyle(Color(hex: 0xD97757)).frame(width: 16, height: 9)
        }
    }

    /// Rows of '#' drawn as pixels, stretched to the frame.
    private struct Sprite: View {
        let rows: [String]
        let pixelHeight: CGFloat
        var body: some View {
            Canvas { ctx, size in
                let w = CGFloat(rows.first?.count ?? 1), h = CGFloat(rows.count)
                let px = size.width / w, py = size.height / h
                var path = Path()
                for (y, row) in rows.enumerated() {
                    for (x, c) in row.enumerated() where c == "#" {
                        path.addRect(CGRect(x: CGFloat(x) * px, y: CGFloat(y) * py, width: px + 0.2, height: py + 0.2))
                    }
                }
                ctx.fill(path, with: .foreground)
            }
            .accessibilityHidden(true)
        }
    }
}

/// One row, in the desktop's columns.
struct SidebarRow: View {
    let session: PerchSession
    var nested = false

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            StatusSlot(state: session.state, asleep: session.asleep)
                .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 }
            AgentMark(session: session)
                .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 }
            VStack(alignment: .leading, spacing: 2) {
                Text(session.title.isEmpty ? "Untitled" : session.title)
                    .font(.callout.weight(.medium))
                    .foregroundStyle(session.asleep ? .secondary : .primary)
                    .lineLimit(1)
                if let note = note {
                    Text(note)
                        .font(.caption)
                        .foregroundStyle(SidebarState.color(session.state))
                        .lineLimit(2)
                }
            }
            Spacer(minLength: 6)
            trailing
        }
        .padding(.leading, nested ? 18 : 0)
        .opacity(session.canSend || session.asleep ? 1 : 0.6)
        .accessibilityElement(children: .combine)
    }

    /// The agent's ask, as the desktop's note line. A permission row says it
    /// with its tag instead (the ask itself is a tap away).
    private var note: String? {
        guard !session.asleep else { return nil }
        if session.state == "waiting" { return session.note ?? "Waiting for your input" }
        if session.state == "permission", let a = session.asking, !a.isEmpty { return a }
        return nil
    }

    @ViewBuilder
    private var trailing: some View {
        if session.asleep {
            Text("asleep").font(.caption2).foregroundStyle(.tertiary)
        } else if session.state == "permission" {
            Text("permission")
                .font(.caption2.weight(.semibold))
                .foregroundStyle(SidebarState.color("permission"))
                .padding(.horizontal, 6).padding(.vertical, 2)
                .background(SidebarState.color("permission").opacity(0.14), in: Capsule())
        } else if session.state == "working", let start = session.turnStartMs {
            TimelineView(.periodic(from: .now, by: 1)) { tl in
                Text(Self.elapsed(tl.date.timeIntervalSince1970 * 1000 - Double(start)))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(SidebarState.color("working"))
            }
        } else if session.state == "done", let at = session.doneAtMs {
            TimelineView(.periodic(from: .now, by: 15)) { tl in
                let ms = tl.date.timeIntervalSince1970 * 1000 - Double(at)
                Text(Self.age(ms))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(Self.warmth(ms))
            }
        }
    }

    /// fmtAge: one unit, "12s" / "4m" / "2h" / "3d".
    static func age(_ ms: Double) -> String {
        let s = max(0, Int(ms / 1000))
        if s < 60 { return "\(s)s" }
        let m = s / 60
        if m < 60 { return "\(m)m" }
        let h = m / 60
        return h < 24 ? "\(h)h" : "\(h / 24)d"
    }

    /// fmtElapsed: "42s" / "3m" / "1h 5m".
    static func elapsed(_ ms: Double) -> String {
        let s = max(0, Int(ms / 1000))
        if s < 60 { return "\(s)s" }
        let m = s / 60
        return m < 60 ? "\(m)m" : "\(m / 60)h \(m % 60)m"
    }

    /// The desktop's warmth: a turn that just came back reads loudest, one
    /// parked since morning quietest.
    static func warmth(_ ms: Double) -> HierarchicalShapeStyle {
        let m = ms / 60000
        return m < 2 ? .primary : m < 10 ? .secondary : m < 60 ? .tertiary : .quaternary
    }
}

/// A project's header: chevron, name, the dot of its most urgent tab, a count.
struct ProjectHeader: View {
    let name: String
    let sessions: [PerchSession]
    let collapsed: Bool
    let toggle: () -> Void

    var body: some View {
        Button(action: toggle) {
            HStack(spacing: 8) {
                Image(systemName: "chevron.right")
                    .font(.caption2.weight(.bold))
                    .foregroundStyle(.tertiary)
                    .rotationEffect(.degrees(collapsed ? 0 : 90))
                    .frame(width: 10)
                Text(name)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(.primary)
                    .textCase(nil)
                let state = SidebarState.aggregate(sessions)
                if state != "idle" {
                    Circle().fill(SidebarState.color(state)).frame(width: 7, height: 7)
                }
                Spacer()
                Text("\(sessions.filter { !$0.asleep }.count)")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.tertiary)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("\(name), \(collapsed ? "collapsed" : "expanded")")
    }
}

/// A lane label inside a project ("Chats", "Sessions"), shown only when a
/// project has both, as on the desktop.
struct LaneLabel: View {
    let text: String
    var body: some View {
        Text(text.uppercased())
            .font(.caption2.weight(.semibold))
            .tracking(0.6)
            .foregroundStyle(.tertiary)
            .frame(maxWidth: .infinity, alignment: .leading)
            // A label on the branch, not a row: no divider, little height.
            .listRowSeparator(.hidden)
            .listRowInsets(EdgeInsets(top: 10, leading: 20, bottom: 0, trailing: 16))
            .accessibilityAddTraits(.isHeader)
    }
}
