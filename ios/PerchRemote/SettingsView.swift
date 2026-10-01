// The phone's settings: reading answers aloud, the paired computers, and
// what version this is.

import SwiftUI

struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        @Bindable var model = model
        NavigationStack {
            Form {
                Section {
                    Toggle("Read answers aloud", isOn: $model.readAloudByDefault)
                    if model.speakerChoiceCount > 0 {
                        Button("Forget each session's choice (\(model.speakerChoiceCount))") {
                            model.resetSpeakerChoices()
                        }
                    }
                } header: {
                    Text("Reading aloud")
                } footer: {
                    Text("For sessions you haven't set. The speaker button in a session turns it on or off there, and the phone remembers that.")
                }

                Section("Computers") {
                    ForEach(model.pairings, id: \.name) { p in
                        ComputerRow(name: p.name)
                            .swipeActions {
                                Button("Forget", role: .destructive) { model.forget(p.name) }
                            }
                    }
                    Button {
                        model.scannerMessage = nil
                        dismiss()
                        model.showScanner = true
                    } label: {
                        Label("Add a computer", systemImage: "plus")
                    }
                }

                Section {
                    LabeledContent("Version", value: Self.version)
                } footer: {
                    Text("Talks to Perch on your computer over your wifi, or anywhere through Tailscale.")
                }
            }
            .navigationTitle("Settings")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("Done") { dismiss() } }
            }
        }
    }

    static var version: String {
        let info = Bundle.main.infoDictionary
        let v = info?["CFBundleShortVersionString"] as? String ?? "?"
        let b = info?["CFBundleVersion"] as? String ?? "?"
        return "\(v) (\(b))"
    }
}
