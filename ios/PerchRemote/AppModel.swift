// Everything the screens show: the paired computers, their sessions (polled
// while the app is in front), what was sent where, and the answers read back.

import Foundation
import Observation

enum Link: Equatable {
    case connecting, online, unreachable, notPaired, slowDown
}

/// Where the navigation stack is: a computer's sessions, or one session.
enum Route: Hashable {
    case computer(String)
    case session(UUID)
}

@Observable @MainActor
final class AppModel {
    private(set) var pairings: [Pairing] = PairingStore.load()
    private(set) var link: [String: Link] = [:]
    /// Each computer's sessions in Perch's own order, keyed by pairing name.
    private(set) var sessions: [String: [PerchSession]] = [:]
    /// The latest answer on screen for a session.
    private(set) var replies: [UUID: String] = [:]
    /// What happened to the last line sent to a session, in plain words.
    private(set) var sendNote: [UUID: String] = [:]
    /// The last line sent to a session from this phone, shown until the
    /// conversation has it.
    private(set) var sent: [UUID: String] = [:]
    /// Each session's conversation, oldest first, once fetched.
    private(set) var history: [UUID: [HistoryItem]] = [:]
    /// Computers whose Perch is too old to hand over a conversation.
    private(set) var noHistory: Set<String> = []
    /// Each computer's projects, for a new tab.
    private(set) var projects: [String: [PerchProject]] = [:]
    /// The permission prompt each waiting session shows.
    private(set) var asks: [UUID: PermissionAsk] = [:]
    /// Tabs made from here that the computer hasn't listed yet.
    private(set) var starting: Set<UUID> = []

    var path: [Route] = []
    /// Why a new session didn't open, until shown.
    var createError: String?

    var showScanner = false
    var scannerMessage: String?
    var pairing = false

    let speaker = Speaker()

    private var clients: [String: PerchClient] = [:]
    private var polling: Task<Void, Never>?
    private var lastState: [UUID: String] = [:]
    /// Sessions a line was sent to whose answer hasn't come back, with the
    /// answer that was there before the line (so an old answer isn't read out).
    private var awaiting: [UUID: (before: String?, since: Date)] = [:]
    private var speakerChoice: [UUID: Bool] = [:]
    private var lastTalkedTo: UUID?
    private var lastSpoken: [UUID: String] = [:]

    init() {
        // UI tests start from a first launch; the Keychain outlives a reinstall.
        if ProcessInfo.processInfo.arguments.contains("-PerchForgetPairings") {
            PairingStore.save([])
            pairings = []
        }
        for p in pairings { clients[p.name] = PerchClient(pairing: p) }
        showScanner = pairings.isEmpty
    }

    // MARK: - lookups

    func session(_ id: UUID) -> PerchSession? {
        for list in sessions.values { if let s = list.first(where: { $0.id == id }) { return s } }
        return nil
    }

    func computer(of id: UUID) -> String? {
        sessions.first { $0.value.contains { $0.id == id } }?.key
    }

    /// How the phone reaches a computer right now: over the wifi or through Tailscale.
    func via(_ name: String) -> String? {
        guard link[name] == .online, let host = clients[name]?.pairing.workingHost else { return nil }
        let parts = host.split(separator: ".").compactMap { Int($0) }
        let tailscale = parts.count == 4 && parts[0] == 100 && (parts[1] & 0xC0) == 64
        return tailscale ? "Tailscale" : "Wi-Fi"
    }

    /// Sessions on a computer that are waiting on you (a question or an approval).
    func needsYou(on name: String) -> Int {
        (sessions[name] ?? []).filter { $0.state == "waiting" || $0.state == "permission" }.count
    }

    func isSpeakerOn(_ id: UUID) -> Bool { speakerChoice[id] ?? (id == lastTalkedTo) }

    func setSpeaker(_ on: Bool, for id: UUID) {
        speakerChoice[id] = on
        if !on { speaker.stop() }
    }

    func isAwaiting(_ id: UUID) -> Bool { awaiting[id] != nil }

    // MARK: - pairing

    /// A scanned or opened `perch://pair` link. Adds a computer, or replaces
    /// the entry for one already paired (its code was renewed).
    func pair(_ url: URL) async {
        guard let p = Pairing(url: url) else {
            scannerMessage = "That isn't a Perch pairing code. Open Perch on your computer, then Settings → Phone."
            showScanner = true
            return
        }
        pairing = true
        defer { pairing = false }
        let client = PerchClient(pairing: p)
        do {
            try await client.connect()
        } catch {
            scannerMessage = message(for: error, computer: p.name)
            showScanner = true
            return
        }
        pairings.removeAll { $0.name == client.pairing.name }
        pairings.append(client.pairing)
        PairingStore.save(pairings)
        clients[p.name] = client
        link[p.name] = .online
        scannerMessage = nil
        showScanner = false
        await refresh(p.name)
    }

    func forget(_ name: String) {
        pairings.removeAll { $0.name == name }
        PairingStore.save(pairings)
        clients[name] = nil
        sessions[name] = nil
        link[name] = nil
        if pairings.isEmpty { showScanner = true }
    }

    // MARK: - polling

    /// Poll while the app is in front, and never behind it.
    func setForeground(_ front: Bool) {
        polling?.cancel()
        polling = nil
        guard front else { return }
        // Back in front: find each computer's address again (the phone may
        // have left the wifi for Tailscale, or come home).
        for p in pairings where link[p.name] != .notPaired { link[p.name] = .connecting }
        polling = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                await self.refreshAll()
                try? await Task.sleep(for: .milliseconds(2500))
            }
        }
    }

    func refreshAll() async {
        await withTaskGroup(of: Void.self) { group in
            for p in pairings { group.addTask { await self.refresh(p.name) } }
        }
    }

    private func refresh(_ name: String) async {
        guard let client = clients[name] else { return }
        // A refused token won't start working by retrying, and retries count
        // toward Perch's lockout.
        if link[name] == .notPaired { return }
        do {
            if link[name] != .online {
                try await client.connect()
                savePairing(client.pairing)
            }
            let list = try await client.sessions()
            guard clients[name] === client else { return }
            link[name] = .online
            sessions[name] = list
            starting.subtract(list.map(\.id))
            for s in list { await observe(s, client: client) }
        } catch let e as PerchError {
            switch e {
            case .notPaired:
                link[name] = .notPaired
                scannerMessage = "The pairing with \(name) was replaced. Scan the code in Perch's Settings → Phone again."
                showScanner = true
            case .slowDown: link[name] = .slowDown
            default: link[name] = .unreachable
            }
        } catch {
            link[name] = .unreachable
        }
    }

    private func savePairing(_ p: Pairing) {
        guard let i = pairings.firstIndex(where: { $0.name == p.name }), pairings[i] != p else { return }
        pairings[i] = p
        PairingStore.save(pairings)
    }

    /// Fetch the answer when a turn ends: for a line sent from here, until an
    /// answer newer than the one before it shows up (a quick turn can start
    /// and end between two polls); otherwise on working → done.
    private func observe(_ s: PerchSession, client: PerchClient) async {
        let prev = lastState[s.id]
        lastState[s.id] = s.state
        let finished = s.state == "done" || s.state == "waiting"
        if let wait = awaiting[s.id] {
            if Date().timeIntervalSince(wait.since) > 30 * 60 { awaiting[s.id] = nil; return }
            guard finished else { return }
            guard let text = try? await client.reply(for: s.id).text, text != wait.before else { return }
            awaiting[s.id] = nil
            sendNote[s.id] = nil
            deliver(text, for: s.id)
        } else if finished, prev == "working", isSpeakerOn(s.id) {
            // Claude may still be writing the transcript when the state flips.
            for _ in 0..<3 {
                if let text = try? await client.reply(for: s.id).text {
                    deliver(text, for: s.id)
                    return
                }
                try? await Task.sleep(for: .seconds(1))
            }
        }
    }

    private func deliver(_ text: String, for id: UUID) {
        replies[id] = text
        guard isSpeakerOn(id), lastSpoken[id] != text else { return }
        lastSpoken[id] = text
        speaker.speak(SpokenText.from(markdown: text))
    }

    /// The answer already there when a session is opened: shown, not spoken.
    func loadReply(_ id: UUID) async {
        guard let name = computer(of: id), let client = clients[name],
              let text = try? await client.reply(for: id).text else { return }
        replies[id] = text
        lastSpoken[id] = lastSpoken[id] ?? text
    }

    func readAloud(_ id: UUID) {
        guard let text = replies[id] else { return }
        lastSpoken[id] = text
        speaker.speak(SpokenText.from(markdown: text))
    }

    /// The computer a session is on, when more than one is paired.
    func computerLabel(of id: UUID) -> String? {
        pairings.count > 1 ? computer(of: id) : nil
    }

    /// What a waiting session asks; cleared once it isn't asking.
    func loadAsk(_ id: UUID) async {
        guard session(id)?.state == "permission" else { asks[id] = nil; return }
        guard let name = computer(of: id), let client = clients[name],
              let ask = try? await client.permission(for: id) else { return }
        asks[id] = ask.asking ? ask : nil
    }

    /// Answer a session's permission prompt. Returns an error to show, or nil.
    func answer(_ id: UUID, _ answer: String, text: String? = nil) async -> String? {
        guard let name = computer(of: id), let client = clients[name] else { return "This tab is gone." }
        do {
            let result = try await client.answer(id, answer, text: text)
            asks[id] = nil
            if answer != "deny" || text != nil { awaiting[id] = (replies[id], Date()); lastTalkedTo = id }
            await refresh(name)
            return result == "answered" ? nil : "It isn't asking any more. It was answered on the computer, or moved on."
        } catch PerchError.sessionGone {
            // A 404 for a tab that's still listed: a Perch from before answering by phone.
            return session(id) == nil ? "That tab was closed in Perch."
                : "Perch on \(name) needs an update to answer from the phone."
        } catch {
            return message(for: error, computer: name)
        }
    }

    /// Fetch a session's conversation. On a Perch too old for it, the screen
    /// falls back to the latest answer alone.
    func loadHistory(_ id: UUID) async {
        guard let name = computer(of: id), !noHistory.contains(name), let client = clients[name] else { return }
        do {
            let items = try await client.history(for: id)
            if history[id] != items { history[id] = items }
            // The line sent from here is in the conversation now.
            if let line = sent[id], items.contains(where: { $0.kind == "user" && Self.plain($0.text) == line }) {
                sent[id] = nil
            }
        } catch PerchError.sessionGone {
            if session(id) != nil { noHistory.insert(name) }
        } catch {}
    }

    /// A line as the person said it: without the tag spoken lines carry, or
    /// the "[Perch #N]" Perch puts in front of a line it types into a tab.
    static func plain(_ text: String) -> String {
        text.replacingOccurrences(of: "[voice input]", with: "")
            .replacingOccurrences(of: #"^\s*\[Perch #\d+\]\s*"#, with: "", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    func loadProjects(_ name: String) async {
        guard let client = clients[name], let list = try? await client.projects() else { return }
        projects[name] = list
    }

    /// Open a Claude tab in a project on a computer, as "New tab" does there,
    /// and go to it. Returns an error to show, or nil.
    func create(on name: String, in project: UUID, named title: String, text: String, voice: Bool) async -> String? {
        guard let client = clients[name] else { return "\(name) isn't paired." }
        let text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        do {
            let id = try await client.create(in: project, name: title.trimmingCharacters(in: .whitespacesAndNewlines),
                                             text: text, voice: voice)
            starting.insert(id)
            if !text.isEmpty {
                sent[id] = text
                awaiting[id] = (nil, Date())
                lastTalkedTo = id
            }
            path.append(.session(id))
            await refresh(name)
            return nil
        } catch PerchError.sessionGone {
            // A 404: the project is gone, or this Perch predates new tabs from the phone.
            return "Couldn't open a tab there. The project may have been removed, or Perch on \(name) needs an update."
        } catch {
            return message(for: error, computer: name)
        }
    }

    // MARK: - talking

    func send(_ text: String, to id: UUID, voice: Bool) async {
        let text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        guard let name = computer(of: id), let client = clients[name] else {
            sendNote[id] = "This tab is gone."
            return
        }
        lastTalkedTo = id
        sent[id] = text
        sendNote[id] = nil
        speaker.stop()
        let before = try? await client.reply(for: id).text
        do {
            let result = try await client.send(text, to: id, voice: voice)
            switch result {
            case "queued", "answered":
                awaiting[id] = (before, Date())
                let asleep = session(id)?.asleep == true
                sendNote[id] = result == "answered" ? "Sent as the answer to its question."
                    : asleep ? "Waking it up. Your message goes in once Claude is running."
                    : nil
            case "unsupported": sendNote[id] = "The phone can only talk to Claude tabs and project chats."
            default: sendNote[id] = nil
            }
        } catch {
            sendNote[id] = message(for: error, computer: name)
            if case PerchError.unreachable = error { link[name] = .unreachable }
        }
    }

    func message(for error: Error, computer name: String) -> String {
        switch error as? PerchError {
        case .notPaired?:
            return "\(name) has a new pairing code. Scan it in Perch's Settings → Phone."
        case .slowDown?:
            return "Too many tries with an old code. Wait a minute, then try again."
        case .sessionGone?:
            return "That tab was closed in Perch."
        case .server(let code)?:
            return "Perch on \(name) had a problem (\(code))."
        case .unreachable?, nil:
            return "Can't reach \(name). Is this iPhone on the same wifi, and is \"Let your phone connect\" on in Perch's Settings → Phone? Away from home, Tailscale has to be on for both."
        }
    }
}
