// The mascot ("Monocle Guy") on his power line, ported from the desktop's
// setup overlay (src/web/src/setup-overlay.ts): he walks in, lands (the wire
// takes a damped bounce), perches, and loops three idle beats in a shuffled
// order: an inquisitive look-around, taking notes on a pad hung on the wire,
// and dozing off until he startles awake. The whole performance is pose(t),
// a pure function of time, as on the desktop; keep the two in step (the
// numbers below are the desktop's, unchanged).

import SwiftUI

enum PerchBird {
    static let W = 320.0, H = 150.0
    static let S = 1.6                      // bird rig scale
    static let perchX = 175.0
    static let walkFrom = 46.0
    static let noteX = 216.0
    static let introMs = 2050.0
    static let idleA = 3600.0, idleB = 4600.0, idleC = 5600.0
    static var idleMs: Double { idleA + idleB + idleC }
    static let restT = 2450.0               // the perched pose, for reduced motion

    // MARK: easing and keyframes

    enum Ease { case lin, out, inOut, back }

    static func clamp01(_ u: Double) -> Double { min(1, max(0, u)) }
    static func ease(_ e: Ease, _ u: Double) -> Double {
        switch e {
        case .lin: return u
        case .out: return 1 - pow(1 - u, 3)
        case .inOut: return u < 0.5 ? 4 * u * u * u : 1 - pow(-2 * u + 2, 3) / 2
        case .back: let c = 1.4; return 1 + (c + 1) * pow(u - 1, 3) + c * pow(u - 1, 2)
        }
    }

    typealias Key = (t: Double, v: Double, e: Ease)
    static func k(_ t: Double, _ v: Double, _ e: Ease = .inOut) -> Key { (t, v, e) }

    /// A keyframe track: the ease names the curve INTO that key; clamped at both ends.
    static func kf(_ t: Double, _ keys: [Key]) -> Double {
        if t <= keys[0].t { return keys[0].v }
        for i in 1..<keys.count where t <= keys[i].t {
            let (t0, v0) = (keys[i - 1].t, keys[i - 1].v)
            let u = ease(keys[i].e, clamp01((t - t0) / (keys[i].t - t0)))
            return v0 + (keys[i].v - v0) * u
        }
        return keys[keys.count - 1].v
    }

    /// A v-shaped blink around `at`, ~140 ms.
    static func blink(_ t: Double, _ at: Double) -> Double {
        let d = abs(t - at)
        return d > 70 ? 1 : max(0.12, d / 70)
    }

    // MARK: the pose

    struct Pose {
        var x = perchX, bob = 0.0, legSwing = 0.0, bodyRot = 0.0, headTilt = 0.0
        var crouch = 0.55, tailAng = 4.0, weight = 2.9, blink = 1.0
        var breathe = 0.0, headDX = 0.0, headDY = 0.0, brow = 0.25
        var noteVis = 0.0, scribble = 0.0, jitter = 0.0, z1 = 0.0, z2 = 0.0, slip = 0.0
    }

    static func shuffledOrder() -> [Int] { [0, 1, 2].shuffled() }

    static func pose(_ t: Double, order: [Int] = [0, 1, 2]) -> Pose {
        if t < introMs { return intro(t) }
        let beats: [(Double, (Double) -> Pose)] = [(idleA, inquisitive), (idleB, notes), (idleC, sleepy)]
        var i = (t - introMs).truncatingRemainder(dividingBy: idleMs)
        for b in order {
            if i < beats[b].0 { return beats[b].1(i) }
            i -= beats[b].0
        }
        return inquisitive(0)
    }

    static func intro(_ t: Double) -> Pose {
        let walkEnd = 1550.0
        let stride = 2 * Double.pi * (t / 430)
        let moving = t < walkEnd ? 1 - clamp01((t - 1250) / 300) : 0
        let crouch = kf(t, [k(1450, 0), k(1650, 1, .out), k(2050, 0.55)])
        var weight = 2.0 + crouch * 1.6
        if t > 1600 {
            let tau = (t - 1600) / 1000
            weight += 1.4 * exp(-3.2 * tau) * cos(12 * tau)
        }
        var p = Pose()
        p.x = kf(t, [k(0, walkFrom), k(walkEnd, perchX, .out)])
        p.bob = -abs(sin(stride)) * 1.7 * moving
        p.legSwing = sin(stride) * 16 * moving
        p.bodyRot = 4 * moving + kf(t, [k(1250, 0), k(1550, -3, .out), k(1900, 0)])
        p.headTilt = sin(stride - 0.9) * 2.5 * moving
        p.crouch = crouch
        p.tailAng = kf(t, [k(1500, 0), k(1680, 16, .back), k(2050, 4)])
        p.weight = weight
        p.blink = blink(t, 950)
        p.brow = kf(t, [k(1450, 0), k(1700, 1, .back), k(2050, 0.25)])
        return p
    }

    static func inquisitive(_ i: Double) -> Pose {
        var p = Pose()
        p.breathe = sin(2 * .pi * (2 * i / idleA)) * 0.012
        p.headTilt = kf(i, [k(0, 0), k(350, -19, .back), k(1100, -19, .lin), k(1500, 11), k(2500, 11, .lin), k(3000, 0), k(3600, 0)])
        p.headDX = kf(i, [k(0, 0), k(350, -0.8, .back), k(1100, -0.8, .lin), k(1500, 1.7), k(2500, 1.7, .lin), k(3000, 0)])
        p.headDY = kf(i, [k(0, 0), k(350, -0.9, .back), k(1100, -0.9, .lin), k(1500, 1.1), k(2500, 1.1, .lin), k(3000, 0)])
        p.bodyRot = kf(i, [k(1100, 0), k(1500, 3.5), k(2500, 3.5), k(3000, 0)])
        p.tailAng = kf(i, [k(2100, 4), k(2260, 15, .back), k(2600, 4, .out)])
        p.blink = min(blink(i, 800), blink(i, 2050))
        p.brow = kf(i, [k(0, 0.25), k(350, 1, .back), k(1100, 1, .lin), k(1500, -0.35), k(2500, -0.35, .lin), k(3000, 0.25)])
        return p
    }

    static func notes(_ n: Double) -> Pose {
        let env = clamp01((n - 600) / 120) * clamp01((1450 - n) / 120)
                + clamp01((n - 2600) / 120) * clamp01((3400 - n) / 120)
        let jitter = env * sin(2 * .pi * n / 115)
        var p = Pose()
        p.breathe = sin(2 * .pi * (2 * n / idleB)) * 0.012
        p.noteVis = kf(n, [k(150, 0), k(550, 1, .out), k(4050, 1, .lin), k(4500, 0)])
        p.scribble = kf(n, [k(600, 0), k(1450, 0.5, .lin), k(2600, 0.5), k(3400, 1, .lin)])
        p.jitter = jitter
        p.headTilt = kf(n, [k(0, 0), k(500, 14), k(1450, 14, .lin), k(1800, -8, .back), k(2250, -8, .lin),
                            k(2600, 14), k(3400, 14, .lin), k(4100, 0)]) + jitter * 1.6
        p.headDX = kf(n, [k(0, 0), k(500, 2.2), k(1450, 2.2, .lin), k(1800, -0.6, .back), k(2250, -0.6, .lin),
                          k(2600, 2.2), k(3400, 2.2, .lin), k(4100, 0)])
        p.headDY = kf(n, [k(0, 0), k(500, 1.6), k(1450, 1.6, .lin), k(1800, -1.0, .back), k(2250, -1.0, .lin),
                          k(2600, 1.6), k(3400, 1.6, .lin), k(4100, 0)]) + jitter * 0.35
        p.bodyRot = kf(n, [k(0, 0), k(500, 4), k(3400, 4, .lin), k(4100, 0)])
        p.tailAng = kf(n, [k(3400, 4), k(3560, 15, .back), k(3900, 4, .out)])
        p.blink = min(blink(n, 2050), blink(n, 3650))
        p.brow = kf(n, [k(0, 0.25), k(500, -0.2), k(1450, -0.2, .lin), k(1800, 0.9, .back), k(2250, 0.9, .lin),
                        k(2600, -0.2), k(3400, -0.2, .lin), k(3700, 0.6, .back), k(4600, 0.25)])
        return p
    }

    static func sleepy(_ s: Double) -> Pose {
        let sway = s > 1500 && s < 3700 ? sin(2 * .pi * (s - 1500) / 1100) * 0.06 : 0
        let lids = kf(s, [k(0, 1), k(1500, 0.45), k(3100, 0.3, .lin), k(3700, 0.12, .out), k(4000, 0.12, .lin),
                          k(4180, 1.05, .back), k(4900, 1, .lin)])
        var p = Pose()
        p.breathe = sin(2 * .pi * (1.5 * s / idleC)) *
            kf(s, [k(0, 0.012), k(1500, 0.024), k(4000, 0.024, .lin), k(4400, 0.012, .out)])
        p.headTilt = kf(s, [k(0, 0), k(1500, 9), k(3100, 12, .lin), k(3700, 18, .out), k(4000, 18, .lin),
                            k(4180, -5, .back), k(4900, -5, .lin), k(5600, 0)])
        p.headDY = kf(s, [k(0, 0), k(1500, 1.6), k(3700, 2.4, .lin), k(4000, 2.4, .lin), k(4180, -0.8, .back),
                          k(4900, -0.8, .lin), k(5600, 0)])
        p.headDX = kf(s, [k(0, 0), k(1500, 0.6), k(3700, 1.0, .lin), k(4000, 1.0, .lin), k(4180, -0.4, .back),
                          k(4900, -0.4, .lin), k(5600, 0)])
        p.bodyRot = kf(s, [k(0, 0), k(1500, 2.5), k(3700, 4, .lin), k(4000, 4, .lin), k(4180, -1, .back),
                           k(4900, -1, .lin), k(5600, 0)])
        p.crouch = 0.55 + kf(s, [k(0, 0), k(1500, 0.25), k(4000, 0.25, .lin), k(4180, -0.1, .back),
                                 k(4900, -0.1, .lin), k(5600, 0)])
        p.weight = 2.9 + kf(s, [k(0, 0), k(1500, 0.4), k(4000, 0.4, .lin), k(4400, 0, .out)])
        p.tailAng = kf(s, [k(0, 4), k(1600, 1), k(4000, 1, .lin), k(4180, 14, .back), k(4600, 4, .out)])
        p.blink = min(lids + sway, blink(s, 5150))
        p.slip = kf(s, [k(0, 0), k(1600, 0.6), k(3700, 1, .lin), k(4000, 1, .lin), k(4230, 0, .back)])
        p.z1 = (s - 2000) / 1300
        p.z2 = (s - 2700) / 1200
        p.brow = kf(s, [k(0, 0.25), k(1500, -0.3), k(3700, -0.45, .lin), k(4000, -0.45, .lin), k(4180, 1, .back),
                        k(4900, 1, .lin), k(5600, 0.25)])
        return p
    }

    // MARK: the rig

    static let K = 2.5                        // mascot units → rig units
    static let feet = CGPoint(x: 12.35, y: 15.7)
    static let eyeInk = Color(red: 0x15 / 255, green: 0x23 / 255, blue: 0x3b / 255)
    static let body = svgPath("M19.9 10.0 C 19.5 7.9, 17.6 6.3, 15.4 6.5 C 13.9 6.5, 12.5 6.8, 11.4 7.4 C 9.4 7.6, 7.4 6.2, 5.7 6.7 C 5.1 6.9, 5.2 7.6, 5.9 8.1 C 7.0 8.9, 7.7 10.1, 8.5 11.1 C 9.6 12.7, 10.9 14.4, 12.6 14.4 C 14.0 14.4, 15.2 13.9, 16.1 13.0 C 17.1 12.4, 18.0 11.9, 18.6 11.2 C 19.1 10.9, 19.6 10.6, 19.9 10.0 Z")
    static let beak = svgPath("M17.4 9.4 L 21.9 10.3 L 18.2 11.35 Z")
    static let chain = svgPath("M17.6 10.8 C 17.8 11.7, 17.5 12.5, 16.9 13.1")
    static let padLine1 = svgPath("M-5.3 6.8 C -3.9 5.9, -2.7 7.5, -1.3 6.7 C 0.1 6.0, 1.4 7.4, 2.8 6.6 C 3.9 6.0, 4.7 6.9, 5.3 6.7")
    static let padLine2 = svgPath("M-5.3 10.8 C -3.9 9.9, -2.7 11.5, -1.3 10.7 C -0.1 10.1, 1.0 11.0, 1.9 10.8")
    static let browN: [Double] = [15.9, 7.3, 16.55, 7.25, 17.3, 7.5, 17.9, 8.0]
    static let browR: [Double] = [15.75, 6.55, 16.6, 6.15, 17.45, 6.35, 18.0, 7.15]

    static func brow(_ r: Double) -> Path {
        let p = zip(browN, browR).map { $0 + ($1 - $0) * r }
        var path = Path()
        path.move(to: CGPoint(x: p[0], y: p[1]))
        path.addCurve(to: CGPoint(x: p[6], y: p[7]), control1: CGPoint(x: p[2], y: p[3]), control2: CGPoint(x: p[4], y: p[5]))
        return path
    }

    /// The absolute M/L/H/V/C/Z subset of SVG path data the art uses.
    static func svgPath(_ d: String) -> Path {
        var path = Path()
        var tokens: [String] = []
        var cur = ""
        for ch in d {
            if ch.isLetter { if !cur.isEmpty { tokens.append(cur); cur = "" }; tokens.append(String(ch)) }
            else if ch == " " || ch == "," { if !cur.isEmpty { tokens.append(cur); cur = "" } }
            else if ch == "-" && !cur.isEmpty && !cur.hasSuffix("e") { tokens.append(cur); cur = "-" }
            else { cur.append(ch) }
        }
        if !cur.isEmpty { tokens.append(cur) }
        var i = 0, cmd = "M", last = CGPoint.zero
        func num() -> Double { defer { i += 1 }; return Double(tokens[i]) ?? 0 }
        func pt() -> CGPoint { CGPoint(x: num(), y: num()) }
        while i < tokens.count {
            if let c = tokens[i].first, c.isLetter { cmd = tokens[i]; i += 1; if cmd == "Z" { path.closeSubpath(); continue } }
            switch cmd {
            case "M": last = pt(); path.move(to: last); cmd = "L"
            case "L": last = pt(); path.addLine(to: last)
            case "H": last = CGPoint(x: num(), y: last.y); path.addLine(to: last)
            case "V": last = CGPoint(x: last.x, y: num()); path.addLine(to: last)
            case "C": let c1 = pt(), c2 = pt(); last = pt(); path.addCurve(to: last, control1: c1, control2: c2)
            default: i += 1
            }
        }
        return path
    }

    // MARK: drawing

    /// Draw the scene for time `t` (ms) fitted to `size`: scaled to the height,
    /// the wire running the full width, the bird centered on it.
    static func draw(_ ctx: inout GraphicsContext, size: CGSize, t: Double, order: [Int],
                     ink: Color, accent: Color, wireOpacity: Double = 0.65) {
        let s = size.height / H
        let sceneW = max(W, size.width / s)
        let dx = (sceneW - W) / 2
        var p = pose(t, order: order)
        p.x += dx
        let noteX = Self.noteX + dx

        func wireBase(_ x: Double) -> Double { 92 + 40 * (x / sceneW) * (1 - x / sceneW) }
        func wireY(_ x: Double) -> Double {
            let d = (x - p.x) / 38
            return wireBase(x) + p.weight * S * exp(-d * d)
        }

        ctx.scaleBy(x: s, y: s)

        // The wire, dipping under the bird.
        var wire = Path()
        var x = 0.0
        while x <= sceneW + 8 {
            let pt = CGPoint(x: x, y: wireY(x))
            if x == 0 { wire.move(to: pt) } else { wire.addLine(to: pt) }
            x += 8
        }
        ctx.stroke(wire, with: .color(ink.opacity(wireOpacity)), style: StrokeStyle(lineWidth: 1.6, lineCap: .round))

        // The note pad, hung on the wire beside the perch.
        if p.noteVis > 0.01 {
            var pad = ctx
            pad.opacity = p.noteVis
            pad.translateBy(x: noteX, y: wireY(noteX) + (1 - p.noteVis) * 2.5)
            pad.rotate(by: .degrees(p.jitter * 2.5))
            var hook = Path(); hook.move(to: .zero); hook.addLine(to: CGPoint(x: 0, y: 2.2))
            pad.stroke(hook, with: .color(ink), style: StrokeStyle(lineWidth: 1.1, lineCap: .round))
            pad.fill(Path(roundedRect: CGRect(x: -8.5, y: 2.2, width: 17, height: 13), cornerRadius: 1.5),
                     with: .color(ink.opacity(0.92)))
            let inkStyle = StrokeStyle(lineWidth: 0.9, lineCap: .round)
            pad.stroke(padLine1.trimmedPath(from: 0, to: clamp01(p.scribble * 2)), with: .color(eyeInk), style: inkStyle)
            pad.stroke(padLine2.trimmedPath(from: 0, to: clamp01(p.scribble * 2 - 1)), with: .color(eyeInk), style: inkStyle)
        }

        // The bird, in mascot coordinates with the feet at the origin.
        let gy = wireY(p.x)
        var bird = ctx
        bird.translateBy(x: p.x, y: gy)
        bird.scaleBy(x: S * K, y: S * K)
        bird.translateBy(x: -feet.x, y: -feet.y)

        let legStyle = StrokeStyle(lineWidth: 1.0, lineCap: .round)
        for (lx, swing) in [(11.3, p.legSwing), (13.4, -p.legSwing)] {
            var leg = bird
            leg.translateBy(x: lx, y: 14.1)
            leg.rotate(by: .degrees(swing))
            var l = Path(); l.move(to: .zero); l.addLine(to: CGPoint(x: 0, y: 1.6))
            leg.stroke(l, with: .color(ink), style: legStyle)
        }

        var trunk = bird
        let tilt = p.bodyRot + p.headTilt * 0.55 - p.tailAng * 0.12
        trunk.translateBy(x: 0, y: (p.bob + p.crouch * 2.2) / K)
        trunk.translateBy(x: 13, y: 11.5)
        trunk.rotate(by: .degrees(tilt))
        trunk.translateBy(x: -13, y: -11.5)
        trunk.translateBy(x: feet.x, y: feet.y)
        trunk.scaleBy(x: 1, y: 1 + p.breathe)
        trunk.translateBy(x: -feet.x, y: -feet.y)
        trunk.fill(body, with: .color(ink))
        trunk.fill(beak, with: .color(ink))

        var face = trunk
        face.translateBy(x: p.headDX * 0.35, y: p.headDY * 0.35)
        face.stroke(brow(p.brow), with: .color(eyeInk), style: StrokeStyle(lineWidth: 0.75, lineCap: .round))
        let ry = 0.95 * p.blink
        face.fill(Path(ellipseIn: CGRect(x: 17 - 0.95, y: 9 - ry, width: 1.9, height: 2 * ry)), with: .color(eyeInk))
        var mono = face
        mono.translateBy(x: p.slip * 0.25, y: p.slip * 0.55)
        mono.stroke(Path(ellipseIn: CGRect(x: 17 - 1.95, y: 9 - 1.95, width: 3.9, height: 3.9)),
                    with: .color(accent), lineWidth: 0.6)
        mono.stroke(chain, with: .color(accent), style: StrokeStyle(lineWidth: 0.5, lineCap: .round))

        // Sleepy z's drifting up off the head.
        let hx = p.x + 19, hy = gy - 27
        for (ph, size) in [(p.z1, 9.0), (p.z2, 7.0)] where ph > 0 && ph < 1 {
            var z = ctx
            z.opacity = sin(.pi * ph) * 0.7
            z.draw(Text("z").font(.system(size: size, weight: .semibold)).italic().foregroundColor(ink),
                   at: CGPoint(x: hx + 6 + ph * 9, y: hy - 6 - ph * 13))
        }
    }
}

/// The bird on his wire, animated (or perched still when Reduce Motion is on).
struct BirdScene: View {
    var ink: Color = Color(red: 0.91, green: 0.94, blue: 0.97)
    var accent: Color = Color(red: 0x76 / 255, green: 0xB9 / 255, blue: 0xED / 255)
    /// Skip the walk-in and start perched.
    var perched = false
    @State private var start = Date()
    @State private var order = PerchBird.shuffledOrder()
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        TimelineView(.animation(paused: reduceMotion)) { tl in
            Canvas { ctx, size in
                let t = reduceMotion ? PerchBird.restT
                    : tl.date.timeIntervalSince(start) * 1000 + (perched ? PerchBird.introMs : 0)
                PerchBird.draw(&ctx, size: size, t: t, order: order, ink: ink, accent: accent)
            }
        }
        .accessibilityHidden(true)
    }
}

/// A loading state: the bird on his wire over a caption with ticking dots.
struct BirdLoading: View {
    let caption: String
    var body: some View {
        VStack(spacing: 4) {
            BirdScene(ink: Color.secondary, perched: false)
                .frame(height: 96)
                .frame(maxWidth: 260)
            TimelineView(.periodic(from: .now, by: 0.4)) { tl in
                let n = Int(tl.date.timeIntervalSinceReferenceDate / 0.4) % 4
                Text(caption + String(repeating: ".", count: n) + String(repeating: " ", count: 3 - n))
                    .font(.subheadline.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity)
    }
}
