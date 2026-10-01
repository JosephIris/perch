// One session as a conversation: everything said to it and by it (its
// prompts, its prose, and its tool calls folded into a line), and an input
// bar like any messaging app: a field with a small mic beside it. Hold the
// mic and let go to send; tap it and it sends after a quiet spell. While it
// listens, the desktop's CRT scope rises above the bar in the tab's color.

import SwiftUI

struct SessionView: View {
    let id: UUID
    @Environment(AppModel.self) private var model
    @State private var listener = Listener()
    @State private var typed = ""
    @State private var voice: VoicePhase = .idle
    @State private var voiceSince = Date()
    @State private var pressedAt: Date?
    @State private var tapToStop = false
    @FocusState private var typing: Bool

    private var session: PerchSession? { model.session(id) }
    private var starting: Bool { session == nil && model.starting.contains(id) }

    var body: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 10) {
                conversation
                Color.clear.frame(height: 1).id("end")
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
        }
        .defaultScrollAnchor(.bottom)
        .scrollDismissesKeyboard(.interactively)
        .background(Color(.systemGroupedBackground))
        .overlay { emptyState }
        .safeAreaInset(edge: .bottom) {
            if let s = session {
                if s.canSend { inputBar(s) } else { unsupportedBar }
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .principal) { header }
            ToolbarItem(placement: .primaryAction) {
                let on = model.isSpeakerOn(id)
                Button { model.setSpeaker(!on, for: id) } label: {
                    Image(systemName: on ? "speaker.wave.2.fill" : "speaker.slash")
                }
                .accessibilityLabel(on ? "Stop reading answers aloud" : "Read answers aloud")
            }
        }
        .task { await followConversation() }
        // A prompt just went up: get what it asks now, not on the next round.
        .onChange(of: session?.state) { _, state in
            if state == "permission" { Task { await model.loadAsk(id) } }
        }
        #if DEBUG
        .task {
            // Screenshots of the scope: PERCH_SCOPE_DEMO=1 runs it on a synthetic voice.
            guard ProcessInfo.processInfo.environment["PERCH_SCOPE_DEMO"] == "1" else { return }
            try? await Task.sleep(for: .seconds(1))
            setVoice(.rec)
            let t0 = Date()
            while !Task.isCancelled {
                listener.feed.writeDemo(at: Date().timeIntervalSince(t0))
                try? await Task.sleep(for: .milliseconds(16))
            }
        }
        #endif
        .onAppear { listener.onDone = { Task { await finishListening() } } }
        .onDisappear { listener.cancel() }
    }

    /// Keep the conversation current while the screen is open: often while
    /// Claude works or an answer is due, now and then otherwise.
    private func followConversation() async {
        await model.loadReply(id)
        var lastState = ""
        var quietRounds = 0
        while !Task.isCancelled {
            let state = session?.state ?? ""
            let busy = state == "working" || model.isAwaiting(id) || model.starting.contains(id)
            if state == "permission" || model.asks[id] != nil { await model.loadAsk(id) }
            if busy || state != lastState || quietRounds >= 3 {
                await model.loadHistory(id)
                quietRounds = 0
            } else {
                quietRounds += 1
            }
            lastState = state
            try? await Task.sleep(for: .milliseconds(2000))
        }
    }

    // MARK: - header

    private var header: some View {
        VStack(spacing: 0) {
            Text(session?.title ?? (starting ? "New session" : ""))
                .font(.subheadline.weight(.semibold))
                .lineLimit(1)
            if let s = session {
                HStack(spacing: 4) {
                    StateDot(state: s.state)
                    Text([StateBadge.label(s.state), s.project, model.computerLabel(of: id)]
                        .compactMap { $0 }.joined(separator: " · "))
                }
                .font(.caption2)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            }
        }
    }

    // MARK: - conversation

    private enum Block: Identifiable {
        case user(Int, String, spoken: Bool)
        case claude(Int, String)
        case tools(Int, [HistoryItem])
        case notice(Int, String)
        var id: Int {
            switch self { case .user(let i, _, _), .claude(let i, _), .tools(let i, _), .notice(let i, _): return i }
        }
    }

    /// History as blocks: consecutive tool calls fold into one row.
    private var blocks: [Block] {
        var out: [Block] = []
        for (i, item) in (model.history[id] ?? []).enumerated() {
            switch item.kind {
            case "user":
                out.append(.user(i, AppModel.plain(item.text), spoken: item.text.contains("[voice input]")))
            case "claude": out.append(.claude(i, item.text))
            case "tool":
                if case .tools(let j, var steps)? = out.last {
                    steps.append(item)
                    out[out.count - 1] = .tools(j, steps)
                } else {
                    out.append(.tools(i, [item]))
                }
            default: out.append(.notice(i, item.text))
            }
        }
        return out
    }

    @ViewBuilder
    private var conversation: some View {
        let blocks = self.blocks
        let lastClaude = blocks.last { if case .claude = $0 { return true } else { return false } }?.id
        // A Perch too old to hand over the conversation: your last line and
        // the latest answer, in the order they happened. Until the answer to
        // that line is in, the answer on screen is the one before it.
        let fallback = blocks.isEmpty ? model.replies[id] : nil
        let answeredLine = model.sent[id] != nil && !model.isAwaiting(id)
        if let reply = fallback, !answeredLine {
            ClaudeMessage(text: reply, isLatest: true, id: id)
        }
        ForEach(blocks) { block in
            switch block {
            case .user(_, let text, let spoken): UserBubble(text: text, spoken: spoken)
            case .claude(let i, let text): ClaudeMessage(text: text, isLatest: i == lastClaude, id: id)
            case .tools(_, let steps): ToolSteps(items: steps)
            case .notice(_, let text):
                Text(text).font(.caption).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)
            }
        }
        if let line = model.sent[id] {
            // With a conversation, this is the line not in it yet.
            UserBubble(text: line, spoken: false).opacity(fallback == nil ? 0.7 : 1)
        }
        if let reply = fallback, answeredLine {
            ClaudeMessage(text: reply, isLatest: true, id: id)
        }
        if let s = session, let note = status(s) {
            HStack(spacing: 6) {
                if s.state == "working" || s.asleep { ProgressView().controlSize(.mini) }
                Text(note)
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            .padding(.top, 2)
        }
    }

    @ViewBuilder
    private var emptyState: some View {
        if starting {
            BirdLoading(caption: "Starting Claude")
        } else if session == nil {
            ContentUnavailableView("Tab closed", systemImage: "xmark.circle",
                                   description: Text("This tab isn't open in Perch any more."))
        } else if (model.history[id] ?? []).isEmpty && model.replies[id] == nil && model.sent[id] == nil {
            ContentUnavailableView {
                Label("Nothing here yet", systemImage: "bubble.left.and.text.bubble.right")
            } description: {
                Text("Type below, or hold the mic and talk. The answer shows up here and is read aloud.")
            }
        }
    }

    /// Where the session is, in words, when that's worth saying.
    private func status(_ s: PerchSession) -> String? {
        if let note = model.sendNote[id] { return note }
        switch s.state {
        case "working": return "Claude is working…"
        case "waiting": return "Claude asked you something. Answer below."
        case "permission": return nil   // the approval card says it
        default: return model.isAwaiting(id) ? "Waiting for Claude…" : nil
        }
    }

    // MARK: - input bar

    private func inputBar(_ s: PerchSession) -> some View {
        VStack(spacing: 8) {
            if s.state == "permission" {
                ApprovalCard(id: id, ask: model.asks[id])
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
            if voice != .idle {
                VoiceScope(phase: voice, phaseStart: voiceSince, listener: listener, phos: PaneColor.of(s.color))
                    .frame(height: 68)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }
            HStack(alignment: .bottom, spacing: 8) {
                ZStack(alignment: .leading) {
                    if listener.isListening {
                        Text(listener.transcript.isEmpty ? "Listening…" : listener.transcript)
                            .foregroundStyle(listener.transcript.isEmpty ? .secondary : .primary)
                            .lineLimit(1...4)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    } else {
                        TextField("Message", text: $typed, axis: .vertical)
                            .lineLimit(1...5)
                            .focused($typing)
                            .accessibilityIdentifier("typeField")
                    }
                }
                .font(.body)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 18, style: .continuous))

                if !typed.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && !listener.isListening {
                    Button(action: sendTyped) {
                        Image(systemName: "arrow.up.circle.fill").font(.system(size: 32))
                    }
                    .accessibilityLabel("Send")
                    .accessibilityIdentifier("sendButton")
                } else {
                    micButton(s)
                }
            }
        }
        .padding(.horizontal, 12)
        .padding(.top, 8)
        .padding(.bottom, 6)
        .background(.bar)
        .animation(.easeOut(duration: 0.2), value: voice)
        .animation(.easeOut(duration: 0.2), value: s.state == "permission")
    }

    private var unsupportedBar: some View {
        Text("The phone can talk to Claude tabs and project chats. Codex and shell tabs aren't supported yet.")
            .font(.footnote)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
            .background(.bar)
    }

    /// Small, beside the field. Hold: talk, let go to send. Tap: hands-free
    /// until a quiet spell; tap again to send sooner.
    private func micButton(_ s: PerchSession) -> some View {
        let recording = listener.isListening
        let tint = recording ? PaneColor.of(s.color) : Color.accentColor
        return Image(systemName: recording ? "stop.fill" : "mic.fill")
            .font(.system(size: 15, weight: .semibold))
            .foregroundStyle(recording ? Color(hex: 0x070D09) : .white)
            .frame(width: 34, height: 34)
            .background(tint, in: Circle())
            .scaleEffect(pressedAt != nil ? 1.12 : 1)
            .animation(.easeOut(duration: 0.12), value: pressedAt != nil)
            .contentShape(Circle().inset(by: -6))
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { _ in
                        guard pressedAt == nil else { return }
                        pressedAt = .now
                        typing = false
                        if listener.isListening {
                            tapToStop = true
                        } else {
                            model.speaker.stop()
                            setVoice(.rec)
                            Task {
                                await listener.start()
                                if let problem = listener.problem { setVoice(.note(problem)); endNoteLater() }
                            }
                        }
                    }
                    .onEnded { _ in
                        let held = Date().timeIntervalSince(pressedAt ?? .now)
                        pressedAt = nil
                        if tapToStop {
                            tapToStop = false
                            Task { await finishListening() }
                        } else if held < 0.3 {
                            listener.handsFree = true
                        } else {
                            Task { await finishListening() }
                        }
                    }
            )
            .sensoryFeedback(.impact(weight: .light), trigger: recording)
            .accessibilityElement()
            .accessibilityLabel(recording ? "Stop and send" : "Dictate")
            .accessibilityAddTraits(.isButton)
            .accessibilityAction {
                if listener.isListening { Task { await finishListening() } }
                else { setVoice(.rec); Task { await listener.start(); listener.handsFree = true } }
            }
    }

    private func setVoice(_ p: VoicePhase) {
        voice = p
        voiceSince = Date()
    }

    private func endNoteLater() {
        Task {
            try? await Task.sleep(for: .milliseconds(1600))
            if case .note = voice { setVoice(.idle) }
        }
    }

    private func finishListening() async {
        guard voice == .rec else { return }
        setVoice(.settling)
        let text = await listener.stop()
        guard !text.trimmingCharacters(in: .whitespaces).isEmpty else {
            setVoice(.note("HEARD NOTHING"))
            endNoteLater()
            return
        }
        setVoice(.sent)
        Task {
            try? await Task.sleep(for: .milliseconds(450))
            if voice == .sent { setVoice(.idle) }
        }
        await model.send(text, to: id, voice: true)
        await model.loadHistory(id)
    }

    private func sendTyped() {
        let text = typed
        typed = ""
        typing = false
        Task {
            await model.send(text, to: id, voice: false)
            await model.loadHistory(id)
        }
    }
}

// MARK: - messages

private struct UserBubble: View {
    let text: String
    let spoken: Bool

    var body: some View {
        HStack {
            Spacer(minLength: 44)
            HStack(alignment: .firstTextBaseline, spacing: 5) {
                if spoken { Image(systemName: "mic.fill").font(.caption2).opacity(0.8) }
                Text(text).textSelection(.enabled)
            }
            .font(.callout)
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .foregroundStyle(.white)
            .background(Color.accentColor, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel((spoken ? "You said: " : "You wrote: ") + text)
    }
}

private struct ClaudeMessage: View {
    let text: String
    let isLatest: Bool
    let id: UUID
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            MarkdownText(text: text)
                .accessibilityElement(children: .combine)
                .accessibilityIdentifier(isLatest ? "replyText" : "")
            if isLatest {
                HStack(spacing: 16) {
                    if model.speaker.isSpeaking {
                        Button("Stop", systemImage: "stop.fill") { model.speaker.stop() }
                    } else {
                        Button("Read aloud", systemImage: "play.fill") { model.readAloud(id) }
                    }
                    Button("Copy", systemImage: "doc.on.doc") { UIPasteboard.general.string = text }
                }
                .font(.caption)
                .buttonStyle(.borderless)
                .labelStyle(.titleAndIcon)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 16, style: .continuous))
    }
}

/// Tool calls in a row, drawn like the desktop's journey rail: one quiet line
/// each, the time, the verb and what it worked on, ×N for a run of the same
/// call. A long run shows its last few with the rest a tap away.
private struct ToolSteps: View {
    let items: [HistoryItem]
    @State private var showAll = false
    private static let shown = 6

    /// Bookkeeping tools the desktop's chat leaves out too.
    private static let quiet: Set<String> = ["TaskCreate", "TaskUpdate", "TaskList", "TaskGet", "TodoWrite", "ToolSearch"]

    private struct Step: Identifiable {
        let id: Int
        let time: String
        let verb: String
        let target: String
        let repeatCount: Int
    }

    private var steps: [Step] {
        let fmt = DateFormatter()
        fmt.dateFormat = "HH:mm"
        return items.enumerated().compactMap { i, item in
            // "Verb target ×N", as Perch's transcript reader writes a work line.
            var text = item.text
            var repeatCount = 1
            if let r = text.range(of: #"\s×(\d+)$"#, options: .regularExpression) {
                repeatCount = Int(text[r].dropFirst(2)) ?? 1
                text.removeSubrange(r)
            }
            let parts = text.split(separator: " ", maxSplits: 1).map(String.init)
            let verb = parts.first ?? ""
            guard !Self.quiet.contains(verb) else { return nil }
            let time = item.atMs.map { fmt.string(from: Date(timeIntervalSince1970: Double($0) / 1000)) } ?? ""
            return Step(id: i, time: time, verb: Self.verbName(verb), target: parts.count > 1 ? parts[1] : "", repeatCount: repeatCount)
        }
    }

    /// MCP tools read as "server · tool", not "mcp__claude_ai_Gmail__send_message".
    private static func verbName(_ verb: String) -> String {
        guard verb.hasPrefix("mcp__") else { return verb }
        let parts = verb.dropFirst(5).components(separatedBy: "__")
        let server = (parts.first ?? "").replacingOccurrences(of: "claude_ai_", with: "")
        return parts.count > 1 ? "\(server) · \(parts[1])" : server
    }

    var body: some View {
        let all = steps
        let hidden = showAll ? 0 : max(0, all.count - Self.shown)
        if !all.isEmpty {
            VStack(alignment: .leading, spacing: 3) {
                if hidden > 0 {
                    Button("\(hidden) earlier step\(hidden == 1 ? "" : "s")") {
                        withAnimation(.easeOut(duration: 0.15)) { showAll = true }
                    }
                    .font(.caption)
                    .padding(.leading, 44)
                }
                ForEach(all.suffix(all.count - hidden)) { s in
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(s.time)
                            .font(.caption2.monospacedDigit())
                            .foregroundStyle(.tertiary)
                            .frame(width: 34, alignment: .trailing)
                        Text("│").font(.caption2).foregroundStyle(.quaternary)
                        Text(s.verb)
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.secondary)
                        Text(s.target)
                            .font(.caption.monospaced())
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .truncationMode(.middle)
                        if s.repeatCount > 1 {
                            Text("×\(s.repeatCount)").font(.caption2.monospacedDigit()).foregroundStyle(.tertiary)
                        }
                    }
                    .accessibilityElement(children: .combine)
                }
            }
            .padding(.vertical, 2)
        }
    }
}
