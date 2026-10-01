// One computer's sessions, laid out like the desktop sidebar in projects
// mode (SidebarViews.swift): projects you can fold, each with its project
// chats first (their threads beneath) and then its sessions. Sleeping tabs
// are hidden unless the toggle at the top shows them; a swipe wakes one.

import SwiftUI

struct SessionListView: View {
    let computer: String
    /// The first screen (only one computer is paired): the bird goes on top.
    let isRoot: Bool
    @Environment(AppModel.self) private var model
    @State private var newSession = false
    @State private var collapsed: Set<String> = []

    private var list: [PerchSession] { model.sessions[computer] ?? [] }
    private var sleepingCount: Int { list.filter(\.asleep).count }

    var body: some View {
        List {
            if isRoot {
                Section { BirdHeader(title: computer, detail: summary) }
            }
            content
        }
        .listSectionSpacing(.compact)
        .environment(\.defaultMinListRowHeight, 38)
        // States change under you (working → done): let rows ease into it.
        .motion(Motion.soft, value: list.map { "\($0.id)\($0.state)\($0.asleep)" })
        .motion(Motion.soft, value: model.showSleeping)
        .motion(Motion.soft, value: collapsed)
        .navigationTitle(isRoot ? "Perch" : computer)
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await model.refreshAll() }
        .toolbar {
            if isRoot {
                ToolbarItem(placement: .topBarLeading) {
                    Button { model.showSettings = true } label: { Image(systemName: "gearshape") }
                        .accessibilityLabel("Settings")
                }
            }
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
        let awake = list.count - sleepingCount
        let need = model.needsYou(on: computer)
        var parts = ["\(awake) session\(awake == 1 ? "" : "s")"]
        if need > 0 { parts.append("\(need) waiting on you") }
        if let via = model.via(computer) { parts.append(via) }
        return parts.joined(separator: " · ")
    }

    @ViewBuilder
    private var content: some View {
        @Bindable var model = model
        switch model.link[computer] ?? .connecting {
        case .online, .connecting where !list.isEmpty:
            if list.isEmpty {
                Section {
                    Button { newSession = true } label: {
                        Label("No sessions yet. Start one", systemImage: "square.and.pencil")
                    }
                }
            }
            if sleepingCount > 0 {
                Section {
                    Toggle(isOn: $model.showSleeping) {
                        Label(model.showSleeping ? "Showing sleeping tabs" : "\(sleepingCount) sleeping tab\(sleepingCount == 1 ? "" : "s") hidden",
                              systemImage: "moon.zzz")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    }
                    .tint(.accentColor)
                }
            }
            ForEach(groups, id: \.name) { g in
                Section {
                    if !collapsed.contains(g.name) { rows(g) }
                } header: {
                    ProjectHeader(name: g.name, sessions: g.all, collapsed: collapsed.contains(g.name)) {
                        if collapsed.contains(g.name) { collapsed.remove(g.name) } else { collapsed.insert(g.name) }
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

    @ViewBuilder
    private func rows(_ g: Group) -> some View {
        let lanes = !g.chats.isEmpty && !g.sessions.isEmpty
        if lanes { LaneLabel(text: "Chats") }
        ForEach(g.chats, id: \.chat.id) { c in
            row(c.chat)
            ForEach(c.threads) { t in row(t, nested: true) }
        }
        if lanes { LaneLabel(text: "Sessions") }
        ForEach(g.sessions) { s in row(s) }
        if !g.sleeping.isEmpty {
            LaneLabel(text: "Sleeping")
            ForEach(g.sleeping) { s in row(s) }
        }
    }

    private func row(_ s: PerchSession, nested: Bool = false) -> some View {
        NavigationLink(value: Route.session(s.id)) { SidebarRow(session: s, nested: nested) }
            .swipeActions(edge: .trailing) {
                if s.state == "permission" && !s.asleep {
                    Button("Deny", role: .destructive) { Task { await answer(s.id, "deny") } }
                }
            }
            .swipeActions(edge: .leading) {
                if s.asleep {
                    Button("Wake") { Task { if let e = await model.wake(s.id) { model.createError = e } } }
                        .tint(.accentColor)
                } else if s.state == "permission" {
                    Button("Allow") { Task { await answer(s.id, "allow") } }.tint(.green)
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

    /// A project's tabs as the desktop files them.
    private struct Group {
        let name: String
        var chats: [(chat: PerchSession, threads: [PerchSession])] = []
        var sessions: [PerchSession] = []
        var sleeping: [PerchSession] = []
        var all: [PerchSession] = []
    }

    /// Projects in the order Perch lists their first tab, tabs outside a
    /// project last. Project chats pin to the top of their project with their
    /// threads under them; sleeping tabs are left out unless shown, and then
    /// go last.
    private var groups: [Group] {
        let show = model.showSleeping
        let ids = Set(list.map(\.id))
        var out: [Group] = []
        func index(_ name: String) -> Int {
            if let i = out.firstIndex(where: { $0.name == name }) { return i }
            out.append(Group(name: name)); return out.count - 1
        }
        for s in list {
            let i = index(s.project ?? "Other")
            out[i].all.append(s)
            if s.asleep && !show { continue }
            if let parent = s.parent, ids.contains(parent) { continue }   // drawn under its chat
            if s.asleep { out[i].sleeping.append(s) }
            else if s.kind == "chat" {
                let threads = list.filter { $0.parent == s.id && (show || !$0.asleep) }
                out[i].chats.append((s, threads))
            } else { out[i].sessions.append(s) }
        }
        // A project with nothing to show (all asleep, hidden) stays out of the way.
        out.removeAll { $0.chats.isEmpty && $0.sessions.isEmpty && $0.sleeping.isEmpty }
        if let i = out.firstIndex(where: { $0.name == "Other" }), list.contains(where: { $0.project == nil }) {
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
                .contentTransition(.interpolate)
                .transition(.scale(scale: 0.8).combined(with: .opacity))
        }
    }
}
