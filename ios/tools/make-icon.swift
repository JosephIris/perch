// Builds the iPhone app icon from the mac one (packaging/perch.icns): iOS wants
// a full-bleed, opaque square and rounds the corners itself, while the mac icon
// is a rounded tile with a margin. The tile is scaled to fill the square over
// a gradient in the tile's own colors, so the corners it no longer covers
// match. Also writes the in-app mark (the same art, used on the pairing screen).
//
//   iconutil -c iconset packaging/perch.icns -o /tmp/perch.iconset
//   swift ios/tools/make-icon.swift /tmp/perch.iconset/icon_512x512@2x.png ios/PerchRemote/Assets.xcassets

import AppKit
import CoreGraphics

let args = CommandLine.arguments
guard args.count == 3, let src = NSImage(contentsOfFile: args[1]),
      let cg = src.cgImage(forProposedRect: nil, context: nil, hints: nil)
else { fatalError("usage: make-icon.swift <mac icon 1024 png> <Assets.xcassets>") }

let w = cg.width, h = cg.height
let rgba = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                     space: CGColorSpace(name: CGColorSpace.sRGB)!,
                     bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
rgba.draw(cg, in: CGRect(x: 0, y: 0, width: w, height: h))
let px = rgba.data!.assumingMemoryBound(to: UInt8.self)
func pixel(_ x: Int, _ y: Int) -> (UInt8, UInt8, UInt8, UInt8) {
    let i = (y * w + x) * 4   // row 0 is the top in this buffer
    return (px[i], px[i + 1], px[i + 2], px[i + 3])
}

// The tile: rows and columns through the middle that are opaque.
let mid = w / 2
var top = 0, bottom = h - 1, left = 0, right = w - 1
while pixel(mid, top).3 < 250 { top += 1 }
while pixel(mid, bottom).3 < 250 { bottom -= 1 }
while pixel(left, h / 2).3 < 250 { left += 1 }
while pixel(right, h / 2).3 < 250 { right -= 1 }
let tile = CGRect(x: left, y: top, width: right - left + 1, height: bottom - top + 1)

func color(_ p: (UInt8, UInt8, UInt8, UInt8)) -> CGColor {
    CGColor(srgbRed: CGFloat(p.0) / 255, green: CGFloat(p.1) / 255, blue: CGFloat(p.2) / 255, alpha: 1)
}
let inset = Int(tile.height * 0.04)
let topColor = color(pixel(mid, top + inset))
let bottomColor = color(pixel(mid, bottom - inset))

func render(size: Int, to path: String) {
    let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: size * 4,
                        space: CGColorSpace(name: CGColorSpace.sRGB)!,
                        bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)!
    let s = CGFloat(size)
    let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB)!,
                              colors: [topColor, bottomColor] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(gradient, start: CGPoint(x: 0, y: s), end: CGPoint(x: 0, y: 0), options: [])
    // Scale so the tile fills the square (CG's origin is bottom-left).
    let k = s / tile.width
    let drawRect = CGRect(x: -tile.minX * k, y: -(CGFloat(h) - tile.maxY) * k,
                          width: CGFloat(w) * k, height: CGFloat(h) * k)
    ctx.interpolationQuality = .high
    // Only the tile itself: outside its rounded corners the mac icon has its
    // drop shadow, which would darken the square's corners.
    let r = s * 0.225
    ctx.addPath(CGPath(roundedRect: CGRect(x: 0, y: 0, width: s, height: s).insetBy(dx: s * 0.004, dy: s * 0.004),
                       cornerWidth: r, cornerHeight: r, transform: nil))
    ctx.clip()
    ctx.draw(cg, in: drawRect)
    let out = NSBitmapImageRep(cgImage: ctx.makeImage()!)
    try! out.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: path))
}

let assets = args[2]
let fm = FileManager.default
let iconDir = assets + "/AppIcon.appiconset"
let markDir = assets + "/AppMark.imageset"
try? fm.createDirectory(atPath: iconDir, withIntermediateDirectories: true)
try? fm.createDirectory(atPath: markDir, withIntermediateDirectories: true)
render(size: 1024, to: iconDir + "/AppIcon.png")
render(size: 360, to: markDir + "/AppMark.png")
print("tile \(tile), wrote \(iconDir) and \(markDir)")
