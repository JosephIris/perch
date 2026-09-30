// One session: its latest answer, a big hold-to-talk button, and a text field.
// Hold to talk and release to send; a tap listens until you pause.

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
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    if let s = session {
                        HStack {
                            StateBadge(state: s.state)
                            if s.asleep {
                                Label("Asleep", systemImage: "moon.zzz.fill")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                            Spacer()
                        }
                    }
                    reply
                }
                .padding()
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .defaultScrollAnchor(.bottom)

            if session?.canSend == false {
                Text("The phone can talk to Claude tabs and project chats. Codex and shell tabs aren't supported yet.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .padding()
            } else if session != nil {
                controls
            } else {
                ContentUnavailableView("Tab closed", systemImage: "xmark.circle",
                                       description: Text("This tab isn't open in Perch any more."))
            }
        }
        .navigationTitle(session?.title ?? "")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
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
        .onAppear {
            listener.onPause = { Task { await finishListening() } }
        }
        .onDisappear { listener.cancel() }
    }

    @ViewBuilder
    private var reply: some View {
        if let text = model.replies[id] {
            Text(markdown(text))
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
            if model.speaker.isSpeaking {
                Button("Stop reading", systemImage: "stop.fill") { model.speaker.stop() }
                    .buttonStyle(.bordered)
                    .controlSize(.small)
            }
        } else {
            Text("No answer yet.")
                .foregroundStyle(.secondary)
        }
    }

    private var controls: some View {
        VStack(spacing: 12) {
            if listener.isListening || !listener.transcript.isEmpty {
                Text(listener.transcript.isEmpty ? "Listening…" : listener.transcript)
                    .font(.title3)
                    .foregroundStyle(listener.transcript.isEmpty ? .secondary : .primary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal)
            } else if let note = listener.problem ?? model.sendNote[id] {
                Text(note)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal)
            } else if model.isAwaiting(id) {
                HStack(spacing: 8) {
                    ProgressView()
                    Text("Waiting for the answer…").foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal)
            }

            talkButton

            HStack {
                TextField("Type instead", text: $typed, axis: .vertical)
                    .lineLimit(1...4)
                    .textFieldStyle(.roundedBorder)
                    .focused($typing)
                    .submitLabel(.send)
                    .onSubmit(sendTyped)
                Button(action: sendTyped) {
                    Image(systemName: "arrow.up.circle.fill").font(.title)
                }
                .disabled(typed.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                .accessibilityLabel("Send")
            }
            .padding(.horizontal)
        }
        .padding(.vertical, 12)
        .background(.bar)
    }

    private var talkButton: some View {
        let listening = listener.isListening
        return ZStack {
            Circle()
                .fill(listening ? Color.red : Color.accentColor)
                .frame(width: 96, height: 96)
                .scaleEffect(pressedAt != nil ? 1.08 : 1)
                .animation(.easeOut(duration: 0.15), value: pressedAt != nil)
            Image(systemName: listening ? "waveform" : "mic.fill")
                .font(.system(size: 36, weight: .semibold))
                .foregroundStyle(.white)
                .symbolEffect(.variableColor.iterative, isActive: listening)
        }
        .contentShape(Circle())
        .gesture(
            DragGesture(minimumDistance: 0)
                .onChanged { _ in
                    guard pressedAt == nil else { return }
                    pressedAt = .now
                    typing = false
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
        .accessibilityLabel(listening ? "Stop and send" : "Hold to talk")
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
        Task { await model.send(text, to: id, voice: false) }
    }

    private func markdown(_ text: String) -> AttributedString {
        (try? AttributedString(markdown: text, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace)))
            ?? AttributedString(text)
    }
}
