// One session, laid out like a conversation: what you last said, where Claude
// is, and its latest answer. The bar at the bottom is for talking: hold the
// microphone and release to send, or tap it and it sends when you pause. A
// text field is there for typing.

import SwiftUI

struct SessionView: View {
    let id: UUID
    @Environment(AppModel.self) private var model
    @State private var listener = Listener()
    @State private var typed = ""
    @State private var pressedAt: Date?
    @State private var tapToStop = false
    @FocusState private var typing: Bool

    private var session: PerchSession? { model.session(id) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                if let s = session { conversation(s) }
            }
            .padding()
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .scrollDismissesKeyboard(.interactively)
        .background(Color(.systemGroupedBackground))
        .overlay {
            if session == nil {
                ContentUnavailableView("Tab closed", systemImage: "xmark.circle",
                                       description: Text("This tab isn't open in Perch any more."))
            } else if model.sent[id] == nil && model.replies[id] == nil {
                ContentUnavailableView {
                    Label("Talk to this session", systemImage: "waveform")
                } description: {
                    Text("Hold the microphone and say what you want. Claude's answer shows up here and is read aloud.")
                }
            }
        }
        .safeAreaInset(edge: .bottom) {
            if let s = session {
                if s.canSend { talkBar } else { unsupportedBar }
            }
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .principal) { header }
            ToolbarItem(placement: .primaryAction) {
                let on = model.isSpeakerOn(id)
                Button {
                    model.setSpeaker(!on, for: id)
                } label: {
                    Image(systemName: on ? "speaker.wave.2.fill" : "speaker.slash")
                }
                .accessibilityLabel(on ? "Stop reading answers aloud" : "Read answers aloud")
            }
        }
        .task { await model.loadReply(id) }
        .onAppear { listener.onPause = { Task { await finishListening() } } }
        .onDisappear { listener.cancel() }
    }

    // MARK: - header

    private var header: some View {
        VStack(spacing: 1) {
            Text(session?.title ?? "")
                .font(.headline)
                .lineLimit(1)
            if let s = session {
                HStack(spacing: 4) {
                    StateDot(state: s.state)
                    Text([StateBadge.label(s.state), s.project, model.computerLabel(of: id)]
                        .compactMap { $0 }.joined(separator: " · "))
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            }
        }
    }

    // MARK: - conversation

    @ViewBuilder
    private func conversation(_ s: PerchSession) -> some View {
        if let line = model.sent[id] {
            HStack {
                Spacer(minLength: 48)
                Text(line)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 10)
                    .foregroundStyle(.white)
                    .background(Color.accentColor, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
            }
            .accessibilityLabel("You said: \(line)")
        }

        if let note = status(s) {
            HStack(spacing: 8) {
                if s.state == "working" || s.asleep { ProgressView().controlSize(.small) }
                Text(note)
            }
            .font(.subheadline)
            .foregroundStyle(.secondary)
        }

        if let text = model.replies[id] {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Label(s.kind == "chat" ? "Project chat" : "Claude", systemImage: "sparkle")
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(.secondary)
                    Spacer()
                    if model.speaker.isSpeaking {
                        Button("Stop", systemImage: "stop.fill") { model.speaker.stop() }
                    } else {
                        Button("Read aloud", systemImage: "play.fill") { model.readAloud(id) }
                    }
                }
                .labelStyle(.titleAndIcon)
                .buttonStyle(.borderless)
                .font(.subheadline)

                Text(markdown(text))
                    .textSelection(.enabled)
                    .accessibilityIdentifier("replyText")
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(16)
            .background(Color(.secondarySystemGroupedBackground),
                        in: RoundedRectangle(cornerRadius: 20, style: .continuous))
        }
    }

    /// Where the session is, in words, when that's worth saying.
    private func status(_ s: PerchSession) -> String? {
        if let note = model.sendNote[id] { return note }
        switch s.state {
        case "working": return "Claude is working…"
        case "waiting": return "Claude asked you something. Answer below."
        case "permission": return "Claude wants approval for a tool. Answer it on the computer."
        default: return model.isAwaiting(id) ? "Sent. Waiting for Claude…" : nil
        }
    }

    // MARK: - talk bar

    private var talkBar: some View {
        VStack(spacing: 12) {
            if listener.isListening || !listener.transcript.isEmpty {
                Text(listener.transcript.isEmpty ? "Listening…" : listener.transcript)
                    .font(.title3)
                    .foregroundStyle(listener.transcript.isEmpty ? .secondary : .primary)
                    .lineLimit(5)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .animation(.default, value: listener.transcript)
            } else if let problem = listener.problem {
                Label(problem, systemImage: "exclamationmark.triangle.fill")
                    .font(.callout)
                    .foregroundStyle(.orange)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }

            if !typing {
                VStack(spacing: 6) {
                    talkButton
                    Text(hint)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .transition(.opacity)
            }

            HStack(alignment: .bottom, spacing: 8) {
                TextField("Type a message", text: $typed, axis: .vertical)
                    .lineLimit(1...5)
                    .focused($typing)
                    .submitLabel(.send)
                    .onSubmit(sendTyped)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 9)
                    .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 20, style: .continuous))
                    .accessibilityIdentifier("typeField")
                if !typed.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    Button(action: sendTyped) {
                        Image(systemName: "arrow.up.circle.fill")
                            .font(.system(size: 34))
                    }
                    .accessibilityLabel("Send")
                    .accessibilityIdentifier("sendButton")
                    .transition(.scale.combined(with: .opacity))
                }
            }
        }
        .padding(.horizontal)
        .padding(.top, 12)
        .padding(.bottom, 8)
        .background(.bar)
        .animation(.easeOut(duration: 0.2), value: typing)
        .animation(.easeOut(duration: 0.15), value: typed.isEmpty)
    }

    private var unsupportedBar: some View {
        Text("The phone can talk to Claude tabs and project chats. Codex and shell tabs aren't supported yet.")
            .font(.callout)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding()
            .background(.bar)
    }

    private var hint: String {
        if !listener.isListening { return "Hold to talk, or tap and pause when you're done" }
        return listener.untilPause ? "Pause to send, or tap to send now" : "Release to send"
    }

    private var talkButton: some View {
        let listening = listener.isListening
        return ZStack {
            Circle()
                .fill(listening ? Color.red : Color.accentColor)
                .frame(width: 72, height: 72)
                .shadow(color: (listening ? Color.red : Color.accentColor).opacity(0.35), radius: 10, y: 4)
                .scaleEffect(pressedAt != nil ? 1.08 : 1)
                .animation(.easeOut(duration: 0.15), value: pressedAt != nil)
            Image(systemName: listening ? "waveform" : "mic.fill")
                .font(.system(size: 28, weight: .semibold))
                .foregroundStyle(.white)
                .symbolEffect(.variableColor.iterative, isActive: listening)
        }
        .contentShape(Circle())
        .gesture(
            DragGesture(minimumDistance: 0)
                .onChanged { _ in
                    guard pressedAt == nil else { return }
                    pressedAt = .now
                    if listener.isListening {
                        // A tap while tap-to-talk is listening sends now.
                        tapToStop = true
                    } else {
                        model.speaker.stop()
                        Task { await listener.start() }
                    }
                }
                .onEnded { _ in
                    let held = Date().timeIntervalSince(pressedAt ?? .now)
                    pressedAt = nil
                    if tapToStop {
                        tapToStop = false
                        Task { await finishListening() }
                    } else if held < 0.35 {
                        listener.untilPause = true
                    } else {
                        Task { await finishListening() }
                    }
                }
        )
        .sensoryFeedback(.impact, trigger: listening)
        .accessibilityLabel(listening ? "Stop and send" : "Talk")
        .accessibilityAddTraits(.isButton)
        .accessibilityAction {
            if listener.isListening { Task { await finishListening() } }
            else { Task { await listener.start(); listener.untilPause = true } }
        }
    }

    private func finishListening() async {
        let text = await listener.stop()
        guard !text.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        await model.send(text, to: id, voice: true)
    }

    private func sendTyped() {
        let text = typed
        typed = ""
        typing = false
        Task { await model.send(text, to: id, voice: false) }
    }

    private func markdown(_ text: String) -> AttributedString {
        (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(text)
    }
}
