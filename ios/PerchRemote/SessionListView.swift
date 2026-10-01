// One computer's sessions, grouped by project, with a way to open a new one.

import SwiftUI

struct SessionListView: View {
    let computer: String
    /// The first screen (only one computer is paired): the bird goes on top.
    let isRoot: Bool
    @Environment(AppModel.self) private var model
    @State private var newSession = false

    private var list: [PerchSession] { model.sessions[computer] ?? [] }

    var body: some View {
        List {
            if isRoot {
                Section { BirdHeader(title: computer, detail: summary) }
            }
            content
        }
        .listSectionSpacing(.compact)
        .environment(\.defaultMinListRowHeight, 40)
        .navigationTitle(isRoot ? "Perch" : computer)
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await model.refreshAll() }
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button { newSession = true } label: { Image(systemName: "square.and.pencil") }
                    .accessibilityLabel("New session")
                    .disabled(model.link[computer] != .online)
            }
            ToolbarItem(placement: .secondaryAction) {
                Button { model.scannerMessage = nil; model.showScanner = true } label: {
                    Label("Add a computer", systemImage: "plus")
                }
            }
            ToolbarItem(placement: .secondaryAction) {
                Button(role: .destructive) { model.forget(computer) } label: {
                    Label("Forget \(computer)", systemImage: "trash")
                }
            }
        }
        .sheet(isPresented: $newSession) {
            NewSessionSheet(computer: computer)
        }
    }

    private var summary: String {
        let need = model.needsYou(on: computer)
        var parts = ["\(list.count) session\(list.count == 1 ? "" : "s")"]
        if need > 0 { parts.append("\(need) waiting on you") }
        if let via = model.via(computer) { parts.append(via) }
        return parts.joined(separator: " · ")
    }

    @ViewBuilder
    private var content: some View {
        switch model.link[computer] ?? .connecting {
        case .online, .connecting where !list.isEmpty:
            if list.isEmpty {
                Section {
                    Button { newSession = true } label: {
                        Label("No sessions yet. Start one", systemImage: "square.and.pencil")
                    }
                }
            }
            ForEach(groups, id: \.project) { g in
                Section(g.project) {
                    ForEach(g.sessions) { s in
                        NavigationLink(value: Route.session(s.id)) { SessionRow(session: s) }
                            .swipeActions(edge: .trailing) {
                                if s.state == "permission" {
                                    Button("Deny", role: .destructive) { Task { await answer(s.id, "deny") } }
                                }
                            }
                            .swipeActions(edge: .leading) {
                                if s.state == "permission" {
                                    Button("Allow") { Task { await answer(s.id, "allow") } }.tint(.green)
                                }
                            }
                    }
                }
            }
        case .connecting:
            Section { BirdLoading(caption: "Connecting").listRowBackground(Color.clear) }
        case .unreachable, .slowDown, .notPaired:
            Section {
                Label(problem, systemImage: "wifi.exclamationmark")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                if model.link[computer] == .notPaired {
                    Button("Scan again") { model.showScanner = true }
                }
            }
        }
    }

    @State private var answerError: String?

    private func answer(_ id: UUID, _ how: String) async {
        answerError = await model.answer(id, how)
        if let e = answerError { model.createError = e }
    }

    private var problem: String {
        switch model.link[computer] {
        case .notPaired?: return model.message(for: PerchError.notPaired, computer: computer)
        case .slowDown?: return model.message(for: PerchError.slowDown, computer: computer)
        default: return model.message(for: PerchError.unreachable, computer: computer)
        }
    }

    private struct Group { let project: String; var sessions: [PerchSession] }

    /// Projects in the order Perch lists their first tab; tabs outside a project last.
    private var groups: [Group] {
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

/// A new Claude tab on this computer, in one of its projects: the same tab
/// "New tab" makes there, shown on the computer and here.
struct NewSessionSheet: View {
    let computer: String
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var project: PerchProject?
    @State private var name = ""
    @State private var first = ""
    @State private var working = false
    @State private var error: String?

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    if let list = model.projects[computer] {
                        Picker("Project", selection: $project) {
                            Text("Choose…").tag(PerchProject?.none)
                            ForEach(list) { Text($0.name).tag(Optional($0)) }
                        }
                    } else {
                        HStack { Text("Project"); Spacer(); ProgressView() }
                    }
                    TextField("Name (optional)", text: $name)
                } footer: {
                    Text("Opens a Claude tab in the project on \(computer), like New tab there.")
                }
                Section("First message (optional)") {
                    TextField("What should Claude do?", text: $first, axis: .vertical)
                        .lineLimit(3...8)
                }
                if let error {
                    Section { Text(error).foregroundStyle(.orange).font(.callout) }
                }
            }
            .navigationTitle("New session")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    if working { ProgressView() } else {
                        Button("Start", action: start).disabled(project == nil)
                    }
                }
            }
            .task {
                await model.loadProjects(computer)
                if project == nil, let only = model.projects[computer], only.count == 1 { project = only.first }
            }
        }
        .presentationDetents([.medium, .large])
    }

    private func start() {
        guard let project else { return }
        working = true
        error = nil
        Task {
            // The sheet goes first so the new session opens on the stack behind it.
            let text = first
            dismiss()
            if let problem = await model.create(on: computer, in: project.id, named: name, text: text, voice: false) {
                model.scannerMessage = nil
                model.createError = problem
            }
        }
    }
}

struct SessionRow: View {
    let session: PerchSession

    var body: some View {
        HStack(spacing: 12) {
            StateDot(state: session.state)
            VStack(alignment: .leading, spacing: 2) {
                Text(session.title.isEmpty ? "Untitled" : session.title)
                    .font(.callout.weight(.medium))
                    .lineLimit(1)
                if session.state == "permission", let asking = session.asking, !asking.isEmpty {
                    Text(asking)
                        .font(.caption.monospaced())
                        .foregroundStyle(.red)
                        .lineLimit(1)
                }
                HStack(spacing: 4) {
                    Text(kindLabel)
                    if session.asleep {
                        Text("·")
                        Image(systemName: "moon.zzz.fill")
                        Text("Asleep")
                    }
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            Spacer(minLength: 8)
            StateBadge(state: session.state)
        }
        .padding(.vertical, 0)
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
