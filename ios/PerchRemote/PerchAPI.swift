// The phone link, the phone's side: pairing from the QR link, and the three
// calls the app makes to Perch on the same wifi. The protocol is
// docs/PHONE-API.md; Perch's side is src/Perch.Core/PhoneServer.cs.
//
// Written on Windows ahead of the Xcode project and not yet compiled. Fix
// what the compiler finds, keep the shapes.

import Foundation

/// What the QR code in Perch's Settings → Phone carries.
struct Pairing: Codable, Equatable {
    var name: String
    var port: Int
    var token: String
    var hosts: [String]
    /// The address that answered last; tried first next time.
    var workingHost: String?

    /// Parses `perch://pair?v=1&name=…&port=…&token=…&hosts=a,b`.
    init?(url: URL) {
        guard url.scheme == "perch", url.host == "pair",
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems
        else { return nil }
        func q(_ k: String) -> String? { items.first { $0.name == k }?.value }
        guard q("v") == "1", let name = q("name"), let port = q("port").flatMap(Int.init),
              let token = q("token"), !token.isEmpty,
              let hosts = q("hosts")?.split(separator: ",").map(String.init), !hosts.isEmpty
        else { return nil }
        self.name = name
        self.port = port
        self.token = token
        self.hosts = hosts
        self.workingHost = nil
    }
}

struct PerchSession: Codable, Identifiable, Equatable {
    let id: UUID
    let title: String
    let project: String?
    /// "claude", "thread", "chat", "codex" or "shell".
    let kind: String
    /// "idle", "working", "done", "waiting" or "permission".
    let state: String
    let canSend: Bool
    let active: Bool
    let asleep: Bool
}

struct PerchReply: Codable, Equatable {
    let text: String?
    let atMs: Int64?
}

enum PerchError: Error, Equatable {
    /// The token was refused: the pairing was replaced ("New code") — scan again.
    case notPaired
    /// Too many wrong tokens from this phone; wait a minute.
    case slowDown
    /// None of the addresses answered: not on the same wifi, or Perch's phone link is off.
    case unreachable
    case sessionGone
    case server(Int)
}

/// One paired Perch. Plain URLSession, no dependencies.
final class PerchClient {
    private(set) var pairing: Pairing
    private let session: URLSession

    init(pairing: Pairing) {
        self.pairing = pairing
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = 4
        cfg.waitsForConnectivity = false
        session = URLSession(configuration: cfg)
    }

    struct Hello: Codable { let app: String; let api: Int; let name: String; let hosts: [String]? }

    /// Find an address that answers. Every address is tried at once: at home
    /// the wifi one wins, away the Tailscale one does, and nobody waits out a
    /// timeout on an address that can't answer. Call on launch, on returning
    /// to the foreground, and after `.unreachable`. Save `pairing` afterwards:
    /// it takes the address list Perch reports now (a Tailscale address added
    /// after pairing shows up here).
    @discardableResult
    func connect() async throws -> Hello {
        let hosts = pairing.hosts
        let winner: (String, Hello)? = try await withThrowingTaskGroup(of: (String, Hello)?.self) { group in
            for host in hosts {
                group.addTask { [self] in
                    do { return (host, try await get("v1/hello", host: host) as Hello) }
                    catch PerchError.notPaired { throw PerchError.notPaired }
                    catch PerchError.slowDown { throw PerchError.slowDown }
                    catch { return nil }
                }
            }
            for try await result in group {
                if let result { group.cancelAll(); return result }
            }
            return nil
        }
        guard let (host, hello) = winner else { throw PerchError.unreachable }
        pairing.workingHost = host
        if let fresh = hello.hosts, !fresh.isEmpty {
            // Keep the address that just worked even if Perch no longer lists it.
            pairing.hosts = fresh.contains(host) ? fresh : fresh + [host]
        }
        return hello
    }

    func sessions() async throws -> [PerchSession] {
        struct List: Codable { let sessions: [PerchSession] }
        let l: List = try await get("v1/sessions")
        return l.sessions
    }

    /// Returns Perch's result word: "queued", "answered", "empty" or "unsupported".
    func send(_ text: String, to id: UUID, voice: Bool) async throws -> String {
        struct Body: Codable { let text: String; let voice: Bool }
        struct Result: Codable { let result: String }
        let r: Result = try await request("POST", "v1/sessions/\(id.uuidString)/send",
                                          body: try JSONEncoder().encode(Body(text: text, voice: voice)))
        return r.result
    }

    func reply(for id: UUID) async throws -> PerchReply {
        try await get("v1/sessions/\(id.uuidString)/reply")
    }

    // MARK: - plumbing

    private func get<T: Decodable>(_ path: String, host: String? = nil) async throws -> T {
        try await request("GET", path, host: host)
    }

    private func request<T: Decodable>(_ method: String, _ path: String, body: Data? = nil, host: String? = nil) async throws -> T {
        guard let h = host ?? pairing.workingHost ?? pairing.hosts.first,
              let url = URL(string: "http://\(h):\(pairing.port)/\(path)")
        else { throw PerchError.unreachable }
        var req = URLRequest(url: url)
        req.httpMethod = method
        req.setValue("Bearer \(pairing.token)", forHTTPHeaderField: "Authorization")
        if let body {
            req.httpBody = body
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let data: Data, resp: URLResponse
        do { (data, resp) = try await session.data(for: req) }
        catch { throw PerchError.unreachable }
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        switch status {
        case 200: return try JSONDecoder().decode(T.self, from: data)
        case 401: throw PerchError.notPaired
        case 404: throw PerchError.sessionGone
        case 429: throw PerchError.slowDown
        default: throw PerchError.server(status)
        }
    }
}
