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
        VStack(spacing: 20) {
            if firstRun {
                VStack(spacing: 8) {
                    Text("Pair with Perch")
                        .font(.largeTitle.bold())
                    Text("On your computer, open Perch, then Settings → Phone, and turn on “Let your phone connect”. Scan the code it shows.")
                        .font(.body)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                }
                .padding(.top, 32)
            } else {
                Text("Scan the code in Perch's Settings → Phone on the computer you want to add.")
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
            }

            ZStack {
                if canScan {
                    QRScanner { payload in
                        guard !model.pairing, let url = URL(string: payload) else { return }
                        Task { await model.pair(url) }
                    }
                } else {
                    ContentUnavailableView("No camera scanner",
                                           systemImage: "qrcode.viewfinder",
                                           description: Text("Copy the pairing link from Perch and paste it here."))
                }
                if model.pairing {
                    ProgressView("Connecting…")
                        .padding()
                        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
                }
            }
            .frame(maxWidth: .infinity)
            .aspectRatio(1, contentMode: .fit)
            .clipShape(RoundedRectangle(cornerRadius: 20))

            if let message = model.scannerMessage {
                Label(message, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange)
                    .font(.callout)
            }

            Spacer()

            Button {
                guard let url = UIPasteboard.general.url
                        ?? UIPasteboard.general.string.flatMap({ URL(string: $0.trimmingCharacters(in: .whitespacesAndNewlines)) })
                else {
                    model.scannerMessage = "There's no pairing link on the clipboard."
                    return
                }
                Task { await model.pair(url) }
            } label: {
                Label("Paste pairing link", systemImage: "doc.on.clipboard")
            }
            .buttonStyle(.bordered)
            .disabled(model.pairing)
        }
        .padding()
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
