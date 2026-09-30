import SwiftUI

@main
struct PerchRemoteApp: App {
    @State private var model = AppModel()
    @Environment(\.scenePhase) private var phase

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model)
                // A pairing code scanned with the Camera app opens here.
                .onOpenURL { url in Task { await model.pair(url) } }
                .onChange(of: phase, initial: true) { _, p in model.setForeground(p == .active) }
                #if DEBUG
                // Pair without the system's "Open in Perch?" prompt, for checks
                // driven from the Mac: SIMCTL_CHILD_PERCH_PAIR_URL=... simctl launch.
                .task {
                    if let s = ProcessInfo.processInfo.environment["PERCH_PAIR_URL"], let url = URL(string: s) {
                        await model.pair(url)
                    }
                }
                #endif
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
            NavigationStack {
                SessionListView()
                    .navigationDestination(for: UUID.self) { SessionView(id: $0) }
            }
            .sheet(isPresented: $model.showScanner) {
                NavigationStack { ScannerScreen(firstRun: false) }
            }
        }
    }
}
