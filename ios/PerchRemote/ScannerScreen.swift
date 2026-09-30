// Pairing: scan the QR code in Perch's Settings → Phone. Where there is no
// camera scanner (the Simulator), a copied perch:// link can be pasted.

import SwiftUI
import VisionKit

struct ScannerScreen: View {
    let firstRun: Bool
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss

    private var canScan: Bool {
        DataScannerViewController.isSupported && DataScannerViewController.isAvailable
    }

    var body: some View {
        ScrollView {
            VStack(spacing: 28) {
                if firstRun { welcome }
                steps
                scanner
                if let message = model.scannerMessage {
                    Label(message, systemImage: "exclamationmark.triangle.fill")
                        .font(.callout)
                        .foregroundStyle(.orange)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                Button(action: paste) {
                    Label("Paste a pairing link", systemImage: "doc.on.clipboard")
                }
                .buttonStyle(.borderless)
                .disabled(model.pairing)
            }
            .padding(.horizontal, 24)
            .padding(.vertical, firstRun ? 40 : 16)
        }
        .background(Color(.systemGroupedBackground))
        .toolbar {
            if !firstRun {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { model.scannerMessage = nil; dismiss() }
                }
            }
        }
        .navigationTitle(firstRun ? "" : "Add a computer")
        .navigationBarTitleDisplayMode(.inline)
    }

    private var welcome: some View {
        VStack(spacing: 14) {
            Image("AppMark")
                .resizable()
                .frame(width: 88, height: 88)
                .clipShape(RoundedRectangle(cornerRadius: 20, style: .continuous))
                .accessibilityHidden(true)
            Text("Connect to Perch")
                .font(.largeTitle.bold())
            Text("Talk to your Claude sessions from your phone, and hear what they say back.")
                .font(.body)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
    }

    private var steps: some View {
        VStack(alignment: .leading, spacing: 14) {
            step(1, "Open Perch on your computer.")
            step(2, "Go to Settings → Phone and turn on “Let your phone connect”.")
            step(3, "Scan the code it shows.")
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func step(_ n: Int, _ text: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text("\(n)")
                .font(.subheadline.weight(.semibold).monospacedDigit())
                .foregroundStyle(.white)
                .frame(width: 24, height: 24)
                .background(Color.accentColor, in: Circle())
                .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 5 }
            Text(text)
                .font(.body)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var scanner: some View {
        ZStack {
            if canScan {
                QRScanner { payload in
                    guard !model.pairing, let url = URL(string: payload) else { return }
                    Task { await model.pair(url) }
                }
            } else {
                Color(.secondarySystemGroupedBackground)
                VStack(spacing: 10) {
                    Image(systemName: "qrcode.viewfinder")
                        .font(.system(size: 44, weight: .light))
                        .foregroundStyle(.secondary)
                    Text("The camera isn't available here. Copy the pairing link from Perch and paste it below.")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, 24)
                }
            }
            if model.pairing {
                ProgressView("Connecting…")
                    .padding(20)
                    .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            }
        }
        .frame(height: 280)
        .frame(maxWidth: .infinity)
        .clipShape(RoundedRectangle(cornerRadius: 24, style: .continuous))
    }

    private func paste() {
        guard let url = UIPasteboard.general.url
                ?? UIPasteboard.general.string.flatMap({ URL(string: $0.trimmingCharacters(in: .whitespacesAndNewlines)) })
        else {
            model.scannerMessage = "There's no pairing link on the clipboard."
            return
        }
        Task { await model.pair(url) }
    }
}

private struct QRScanner: UIViewControllerRepresentable {
    let found: (String) -> Void

    func makeUIViewController(context: Context) -> DataScannerViewController {
        let vc = DataScannerViewController(
            recognizedDataTypes: [.barcode(symbologies: [.qr])],
            qualityLevel: .balanced,
            isHighlightingEnabled: true)
        vc.delegate = context.coordinator
        try? vc.startScanning()
        return vc
    }

    func updateUIViewController(_ vc: DataScannerViewController, context: Context) {
        context.coordinator.found = found
    }

    static func dismantleUIViewController(_ vc: DataScannerViewController, coordinator: Coordinator) {
        vc.stopScanning()
    }

    func makeCoordinator() -> Coordinator { Coordinator(found: found) }

    final class Coordinator: NSObject, DataScannerViewControllerDelegate {
        var found: (String) -> Void
        init(found: @escaping (String) -> Void) { self.found = found }

        func dataScanner(_ scanner: DataScannerViewController, didAdd items: [RecognizedItem], allItems: [RecognizedItem]) {
            for case .barcode(let code) in items {
                if let payload = code.payloadStringValue, payload.hasPrefix("perch://") { found(payload) }
            }
        }
    }
}
