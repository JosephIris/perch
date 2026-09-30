// The sessions on every paired computer, grouped by project.

import SwiftUI

struct SessionListView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        List {
            ForEach(model.pairings, id: \.name) { p in
                computerSections(p.name)
            }
        }
        .navigationTitle("Sessions")
        .refreshable { await model.refreshAll() }
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    Button { model.scannerMessage = nil; model.showScanner = true } label: {
                        Label("Add a computer", systemImage: "plus")
                    }
                    ForEach(model.pairings, id: \.name) { p in
                        Button(role: .destructive) { model.forget(p.name) } label: {
                            Label("Forget \(p.name)", systemImage: "trash")
                        }
                    }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
            }
        }
    }

    @ViewBuilder
    private func computerSections(_ name: String) -> some View {
        let many = model.pairings.count > 1
        switch model.link[name] ?? .connecting {
        case .online:
            let list = model.sessions[name] ?? []
            if list.isEmpty {
                Section(many ? name : "") {
                    Text("No sessions open in Perch.").foregroundStyle(.secondary)
                }
            }
            ForEach(groups(list), id: \.project) { g in
                Section {
                    ForEach(g.sessions) { s in
                        NavigationLink(value: s.id) { SessionRow(session: s) }
                    }
                } header: {
                    Text(many ? "\(g.project) · \(name)" : g.project)
                }
            }
        case .connecting where model.sessions[name] == nil:
            Section(name) {
                HStack(spacing: 12) {
                    ProgressView()
                    Text("Connecting…").foregroundStyle(.secondary)
                }
            }
        case .connecting:
            // Reconnecting after the app came back to the front: keep showing
            // what was there.
            ForEach(groups(model.sessions[name] ?? []), id: \.project) { g in
                Section(many ? "\(g.project) · \(name)" : g.project) {
                    ForEach(g.sessions) { s in
                        NavigationLink(value: s.id) { SessionRow(session: s) }
                    }
                }
            }
        case .unreachable, .slowDown, .notPaired:
            Section(name) {
                Label(problem(name), systemImage: "wifi.exclamationmark")
                    .foregroundStyle(.secondary)
                if model.link[name] == .notPaired {
                    Button("Scan again") { model.showScanner = true }
                }
            }
        }
    }

    private func problem(_ name: String) -> String {
        switch model.link[name] {
        case .notPaired?: return model.message(for: PerchError.notPaired, computer: name)
        case .slowDown?: return model.message(for: PerchError.slowDown, computer: name)
        default: return model.message(for: PerchError.unreachable, computer: name)
        }
    }

    private struct Group { let project: String; var sessions: [PerchSession] }

    /// Projects in the order Perch lists their first tab; tabs outside a project last.
    private func groups(_ list: [PerchSession]) -> [Group] {
        var out: [Group] = []
        for s in list {
            let key = s.project ?? "Other"
            if let i = out.firstIndex(where: { $0.project == key }) { out[i].sessions.append(s) }
            else { out.append(Group(project: key, sessions: [s])) }
        }
        if let i = out.firstIndex(where: { $0.project == "Other" }), list.contains(where: { $0.project == nil }) {
            out.append(out.remove(at: i))
        }
        return out
    }
}

struct SessionRow: View {
    let session: PerchSession

    var body: some View {
        HStack(spacing: 12) {
            StateDot(state: session.state)
            VStack(alignment: .leading, spacing: 2) {
                Text(session.title.isEmpty ? "Untitled" : session.title)
                    .font(.body.weight(.medium))
                    .lineLimit(1)
                HStack(spacing: 4) {
                    Text(kindLabel)
                    if session.asleep {
                        Text("·")
                        Image(systemName: "moon.zzz.fill")
                        Text("Asleep")
                    }
                }
                .font(.subheadline)
                .foregroundStyle(.secondary)
            }
            Spacer(minLength: 8)
            StateBadge(state: session.state)
        }
        .padding(.vertical, 2)
        .opacity(session.canSend ? 1 : 0.5)
        .accessibilityElement(children: .combine)
    }

    private var kindLabel: String {
        switch session.kind {
        case "thread": "Thread"
        case "chat": "Project chat"
        case "codex": "Codex"
        case "shell": "Shell"
        default: "Claude"
        }
    }
}

/// A small colored dot for where a session is; a spinner while it works.
struct StateDot: View {
    let state: String

    var body: some View {
        if state == "working" {
            ProgressView().controlSize(.mini).frame(width: 10, height: 10)
        } else {
            Circle()
                .fill(StateBadge.color(state).opacity(state == "idle" ? 0.4 : 1))
                .frame(width: 8, height: 8)
                .frame(width: 10, height: 10)
        }
    }
}

/// The state in words, for the states that ask something of you or are news.
/// Idle says nothing.
struct StateBadge: View {
    let state: String

    static func label(_ state: String) -> String {
        switch state {
        case "working": "Working"
        case "done": "Done"
        case "waiting": "Waiting on you"
        case "permission": "Needs approval"
        default: "Idle"
        }
    }

    static func color(_ state: String) -> Color {
        switch state {
        case "working": .blue
        case "done": .green
        case "waiting": .orange
        case "permission": .red
        default: .gray
        }
    }

    var body: some View {
        if state != "idle" {
            let color = Self.color(state)
            Text(Self.label(state))
                .font(.caption.weight(.semibold))
                .padding(.horizontal, 8)
                .padding(.vertical, 3)
                .foregroundStyle(color)
                .background(color.opacity(0.15), in: Capsule())
        }
    }
}
