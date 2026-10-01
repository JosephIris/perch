import SwiftUI

@main
struct PerchRemoteApp: App {
    @State private var model = AppModel()
    @Environment(\.scenePhase) private var phase

    var body: some Scene {
        WindowGroup {
            ZStack {
                RootView()
                #if DEBUG
                // PERCH_MD_SAMPLE=1: a sample answer, to check how markdown draws.
                if ProcessInfo.processInfo.environment["PERCH_MD_SAMPLE"] == "1" {
                    ScrollView {
                        MarkdownText(text: MarkdownSample.text).padding()
                    }
                    .background(Color(.systemGroupedBackground))
                    .zIndex(2)
                }
                #endif
                SplashOverlay().zIndex(1)
            }
            .environment(model)
            // A pairing code scanned with the Camera app opens here.
            .onOpenURL { url in Task { await model.pair(url) } }
            .onChange(of: phase, initial: true) { _, p in model.setForeground(p == .active) }
            #if DEBUG
            // Pair without the system's "Open in Perch?" prompt, for checks
            // driven from the Mac: SIMCTL_CHILD_PERCH_PAIR_URL=... simctl launch.
            .task {
                let env = ProcessInfo.processInfo.environment
                if let s = env["PERCH_PAIR_URL"], let url = URL(string: s) {
                    await model.pair(url)
                }
                // And open a session (for screenshots): PERCH_OPEN_SESSION=<id>,
                // or the first Claude tab with PERCH_OPEN_FIRST=1.
                if let s = env["PERCH_OPEN_SESSION"], let id = UUID(uuidString: s) {
                    model.path = [.session(id)]
                } else if env["PERCH_OPEN_FIRST"] == "1" {
                    for _ in 0..<20 {
                        if let s = model.sessions.values.flatMap({ $0 }).first(where: { $0.canSend }) {
                            model.path = [.session(s.id)]
                            break
                        }
                        try? await Task.sleep(for: .milliseconds(500))
                    }
                }
            }
            #endif
        }
    }
}

/// The brand tile's navy, top and bottom (the app icon's gradient).
enum Brand {
    static let top = Color(hex: 0x1E2D4C)
    static let bottom = Color(hex: 0x111A2C)
    static let ink = Color(hex: 0xE8EEF7)
    static let accent = Color(hex: 0x76B9ED)
    static var tile: LinearGradient { LinearGradient(colors: [top, bottom], startPoint: .top, endPoint: .bottom) }
}

/// The splash over the app at launch: up while the bird walks in and lands,
/// then a fade (and a slight zoom) to what's under it. Its own view, because
/// state changed in the App struct doesn't animate.
struct SplashOverlay: View {
    @State private var shown = true
    @State private var gone = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        if !gone {
            SplashView()
                .opacity(shown ? 1 : 0)
                .scaleEffect(shown ? 1 : 1.06)
                .allowsHitTesting(shown)
                .task {
                    try? await Task.sleep(for: .milliseconds(reduceMotion ? 700 : 2300))
                    withAnimation(.easeInOut(duration: 0.6)) { shown = false }
                    try? await Task.sleep(for: .milliseconds(700))
                    gone = true
                }
        }
    }
}

struct SplashView: View {
    var body: some View {
        ZStack {
            Brand.tile.ignoresSafeArea()
            VStack(spacing: 6) {
                BirdScene(ink: Brand.ink, accent: Brand.accent)
                    .frame(height: 170)
                Text("Perch")
                    .font(.system(size: 30, weight: .medium))
                    .foregroundStyle(Brand.ink)
            }
            .offset(y: -30)
        }
    }
}

struct RootView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var model = model
        if model.pairings.isEmpty {
            ScannerScreen(firstRun: true)
        } else {
            NavigationStack(path: $model.path) {
                Group {
                    // One computer: straight to its sessions. More: pick one first.
                    if model.pairings.count == 1, let only = model.pairings.first {
                        SessionListView(computer: only.name, isRoot: true)
                    } else {
                        ComputersView()
                    }
                }
                .navigationDestination(for: Route.self) { route in
                    switch route {
                    case .computer(let name): SessionListView(computer: name, isRoot: false)
                    case .session(let id): SessionView(id: id)
                    }
                }
            }
            .sheet(isPresented: $model.showScanner) {
                NavigationStack { ScannerScreen(firstRun: false) }
            }
            .sheet(isPresented: $model.showSettings) { SettingsView() }
            .alert("Couldn't start a session", isPresented: Binding(
                get: { model.createError != nil }, set: { if !$0 { model.createError = nil } })) {
                Button("OK", role: .cancel) {}
            } message: {
                Text(model.createError ?? "")
            }
        }
    }
}

/// The bird on his wire over a line about what's going on; the first row of
/// the main lists, so it scrolls away and comes back at the top.
struct BirdHeader: View {
    let title: String
    let detail: String
    /// The computer's kind, beside its name (a Mac's laptop, a PC).
    var symbol: String? = nil

    var body: some View {
        ZStack(alignment: .bottomLeading) {
            Brand.tile
            BirdScene(ink: Brand.ink, accent: Brand.accent, perched: true)
                .padding(.bottom, 18)
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 6) {
                    if let symbol { Image(systemName: symbol).font(.subheadline.weight(.semibold)) }
                    Text(title).font(.headline)
                }
                Text(detail).font(.caption).opacity(0.75)
            }
            .foregroundStyle(Brand.ink)
            .padding(.horizontal, 14)
            .padding(.bottom, 10)
        }
        .frame(height: 132)
        .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
        .listRowInsets(EdgeInsets())
        .listRowBackground(Color.clear)
    }
}

struct ComputersView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        List {
            Section {
                BirdHeader(title: "Your computers", detail: summary)
            }
            Section {
                ForEach(model.pairings, id: \.name) { p in
                    NavigationLink(value: Route.computer(p.name)) { ComputerRow(name: p.name) }
                        .swipeActions {
                            Button("Forget", role: .destructive) { model.forget(p.name) }
                        }
                }
            }
        }
        .listSectionSpacing(.compact)
        .environment(\.defaultMinListRowHeight, 40)
        .navigationTitle("Perch")
        .navigationBarTitleDisplayMode(.inline)
        .refreshable { await model.refreshAll() }
        .toolbar {
            ToolbarItem(placement: .topBarLeading) {
                Button { model.showSettings = true } label: { Image(systemName: "gearshape") }
                    .accessibilityLabel("Settings")
            }
            ToolbarItem(placement: .primaryAction) {
                Button { model.scannerMessage = nil; model.showScanner = true } label: {
                    Image(systemName: "plus")
                }
                .accessibilityLabel("Add a computer")
            }
        }
    }

    private var summary: String {
        let all = model.pairings.flatMap { model.sessions[$0.name] ?? [] }
        let need = model.pairings.reduce(0) { $0 + model.needsYou(on: $1.name) }
        return "\(all.count) session\(all.count == 1 ? "" : "s")" + (need > 0 ? " · \(need) waiting on you" : "")
    }
}

struct ComputerRow: View {
    let name: String
    @Environment(AppModel.self) private var model

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: icon)
                .font(.system(size: 14, weight: .medium))
                .foregroundStyle(.white)
                .frame(width: 30, height: 30)
                .background(tile, in: RoundedRectangle(cornerRadius: 7, style: .continuous))
            VStack(alignment: .leading, spacing: 1) {
                Text(name).font(.callout.weight(.medium)).lineLimit(1)
                Text(status).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            Spacer(minLength: 8)
            let need = model.needsYou(on: name)
            if need > 0 {
                Text("\(need)")
                    .contentTransition(.numericText())
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 7).padding(.vertical, 2)
                    .background(Color.orange, in: Capsule())
                    .accessibilityLabel("\(need) waiting on you")
            }
        }
    }

    /// A Mac and a PC tell apart at a glance: their own symbol and tile.
    private var icon: String { Computer.symbol(model.os[name]) }

    private var tile: Color {
        switch model.os[name] {
        case "mac": return Color(hex: 0x8E8E93)
        case "windows": return Color(hex: 0x0078D4)
        default: return .accentColor
        }
    }

    private var kind: String? { Computer.kind(model.os[name]) }

    private var status: String {
        switch model.link[name] ?? .connecting {
        case .online:
            let n = (model.sessions[name] ?? []).filter { !$0.asleep }.count
            return [kind, model.via(name), "\(n) session\(n == 1 ? "" : "s")"].compactMap { $0 }.joined(separator: " · ")
        case .connecting: return "Connecting…"
        case .unreachable: return "Can't reach it"
        case .notPaired: return "Scan its code again"
        case .slowDown: return "Wait a minute"
        }
    }
}

#if DEBUG
enum MarkdownSample {
    static let text = """
    ## What changed

    Perch **v1.87.0** is out, with *three* fixes and `LaunchUnsent`. See [the release](https://github.com).

    - **Approvals:** from the phone
      - Allow, deny, *always*
      - Deny with a note
    - Conversations, in full
    1. Pull
    2. Build with `./scripts/build.ps1`

    > The Windows gate hasn't run yet.

    | Check | Result |
    |---|---|
    | Mac gate | 22/22 |
    | .NET tests | 780 |

    ```swift
    func deny(_ t: Session, text: String?) -> Bool {
        guard let p = pane(t) else { return false }  // a long comment that runs well past the edge of the phone
        return true
    }
    ```

    ---

    Done.
    """
}
#endif

/// How a computer is named and drawn by its OS (from its hello).
enum Computer {
    static func symbol(_ os: String?) -> String {
        switch os {
        case "mac": return "laptopcomputer"
        case "windows": return "pc"
        default: return "desktopcomputer"
        }
    }

    static func kind(_ os: String?) -> String? {
        switch os {
        case "mac": return "Mac"
        case "windows": return "Windows PC"
        default: return nil
        }
    }
}
