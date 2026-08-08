// Compose the macOS app icon: src/web/perch-logo.png (the rounded-squircle
// bird art) centered on a transparent canvas with the standard Big-Sur-style
// margins — the icon body occupies ~80.5% of the canvas, like every stock
// app. Usage: swift gen-mac-icon.swift <logo.png> <outdir> ; writes the full
// iconset (16…512 @1x/@2x).
import AppKit
import Foundation

let args = CommandLine.arguments
guard args.count == 3, let logo = NSImage(contentsOfFile: args[1]) else {
    FileHandle.standardError.write("usage: gen-mac-icon.swift <logo.png> <outdir>\n".data(using: .utf8)!)
    exit(2)
}
let outDir = args[2]
try? FileManager.default.createDirectory(atPath: outDir, withIntermediateDirectories: true)

// Render at exact pixel sizes via an offscreen bitmap instead (avoids retina
// doubling from lockFocus on a 2x display).
func renderExact(_ px: Int, _ name: String) {
    guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return }
    let ctx = NSGraphicsContext(bitmapImageRep: rep)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = ctx
    let body = Double(px) * 0.805
    let inset = (Double(px) - body) / 2
    logo.draw(in: NSRect(x: inset, y: inset, width: body, height: body),
              from: .zero, operation: .sourceOver, fraction: 1.0)
    NSGraphicsContext.restoreGraphicsState()
    guard let png = rep.representation(using: .png, properties: [:]) else { return }
    try? png.write(to: URL(fileURLWithPath: "\(outDir)/\(name)"))
}

for s in [16, 32, 64, 128, 256, 512] {
    renderExact(s, "icon_\(s)x\(s).png")
    renderExact(s * 2, "icon_\(s)x\(s)@2x.png")
}
print("iconset written to \(outDir)")
