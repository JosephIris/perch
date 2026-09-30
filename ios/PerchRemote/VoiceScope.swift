// The dictation scope, as on the desktop (src/web/src/voice.ts): while you
// talk, an oscilloscope in the tab's color tag with a phosphor trace that
// lingers and fades; a sweeping dot while the last words settle; a TV
// power-off when the words go in. The one retro surface in the app, on purpose.

import SwiftUI

enum VoicePhase: Equatable {
    case idle
    case rec
    case settling          // the recognizer's last words (the desktop's "decoding")
    case sent
    case note(String)
}

/// The pane color tags (the page's --color-pane-tag-N), and the amber the
/// scope falls back to.
enum PaneColor {
    static let tags: [Color] = [0x76B9ED, 0x6CCB5F, 0xFCE100, 0xFF9E5E, 0xFF99A4, 0xC792EA].map(Color.init(hex:))
    static let amber = Color(hex: 0xFFB000)
    static func of(_ index: Int?) -> Color {
        guard let i = index, tags.indices.contains(i) else { return amber }
        return tags[i]
    }
}

extension Color {
    init(hex: Int) {
        self.init(red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255, blue: Double(hex & 0xFF) / 255)
    }
}

/// Auto-gain and the afterglow, carried between frames.
private final class ScopeState {
    var gain: Float = 1
    var trail: [[Float]] = []
}

struct VoiceScope: View {
    let phase: VoicePhase
    let phaseStart: Date
    let listener: Listener
    let phos: Color
    @State private var state = ScopeState()

    var body: some View {
        TimelineView(.animation) { tl in
            let now = tl.date
            let (label, hint) = texts(now)
            ZStack(alignment: .topLeading) {
                Canvas { ctx, size in draw(&ctx, size: size, now: now) }
                // The tube over the trace; the words ride above it, as on the desktop.
                grid
                scanlines
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .strokeBorder(Color.black.opacity(0.9), lineWidth: 10).blur(radius: 8)
                    .allowsHitTesting(false)
                Text(label)
                    .padding(.top, 5).padding(.leading, 12)
                Text(hint)
                    .opacity(0.6)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomTrailing)
                    .padding(.bottom, 5).padding(.trailing, 12)
            }
            .font(.system(size: 10, design: .monospaced))
            .tracking(0.5)
            .foregroundStyle(phos)
        }
        .background(Color(hex: 0x070D09))
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(phos.opacity(0.3), lineWidth: 1))
        .accessibilityElement()
        .accessibilityLabel(phase == .rec ? "Listening" : "")
    }

    private var grid: some View {
        Canvas { ctx, size in
            var p = Path()
            var x = 0.0
            while x < size.width { p.move(to: CGPoint(x: x, y: 0)); p.addLine(to: CGPoint(x: x, y: size.height)); x += 20 }
            var y = 0.0
            while y < size.height { p.move(to: CGPoint(x: 0, y: y)); p.addLine(to: CGPoint(x: size.width, y: y)); y += 17 }
            ctx.stroke(p, with: .color(phos.opacity(0.08)), lineWidth: 1)
        }
        .allowsHitTesting(false)
    }

    private var scanlines: some View {
        Canvas { ctx, size in
            var y = 2.0
            while y < size.height { ctx.fill(Path(CGRect(x: 0, y: y, width: size.width, height: 1)), with: .color(.black.opacity(0.35))); y += 3 }
        }
        .allowsHitTesting(false)
    }

    private func texts(_ now: Date) -> (String, String) {
        let blink = now.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 1) < 0.6 ? "●" : " "
        switch phase {
        case .rec:
            let secs = Int(now.timeIntervalSince(listener.startedAt))
            var label = "\(blink) \(listener.handsFree ? "HANDS-FREE" : "REC") \(secs / 60):\(String(format: "%02d", secs % 60))"
            let quiet = listener.quietFor
            if listener.handsFree && quiet > Listener.showQuiet {
                label += "  · sending in \(String(format: "%.1f", max(0, Listener.quietSeconds - quiet)))s"
            }
            return (label, listener.handsFree ? "tap: send · talk: keep going" : "release: send")
        case .settling:
            let dots = String(repeating: ".", count: Int(now.timeIntervalSinceReferenceDate / 0.2) % 4)
            return ("DECODING" + dots, "speech · on this iPhone")
        case .sent: return ("SENT", "")
        case .note(let text): return (text, "")
        case .idle: return ("", "")
        }
    }

    private func draw(_ ctx: inout GraphicsContext, size: CGSize, now: Date) {
        let W = size.width, H = size.height
        let mid = H / 2 + 3, amp = H / 2 - 12
        let e = now.timeIntervalSince(phaseStart) * 1000

        func tracePath(_ wave: [Float], _ k: Float) -> Path {
            var p = Path()
            let n = max(1, Int(W))
            for i in 0...n {
                let v = max(-1, min(1, wave[min(wave.count - 1, i * wave.count / (n + 1))] * state.gain)) * k
                let pt = CGPoint(x: Double(i), y: mid - Double(v) * amp)
                if i == 0 { p.move(to: pt) } else { p.addLine(to: pt) }
            }
            return p
        }
        func glowStroke(_ p: Path, opacity: Double, width: Double = 1.6) {
            var g = ctx
            g.opacity = opacity
            var blur = g
            blur.addFilter(.blur(radius: 4))
            blur.stroke(p, with: .color(phos), lineWidth: width + 1.5)
            g.stroke(p, with: .color(phos), lineWidth: width)
        }
        func flat(_ alpha: Double) {
            var p = Path(); p.move(to: CGPoint(x: 0, y: mid)); p.addLine(to: CGPoint(x: W, y: mid))
            var g = ctx; g.opacity = alpha
            g.stroke(p, with: .color(phos), lineWidth: 1.6)
        }

        switch phase {
        case .rec:
            let (wave, _, peak) = listener.feed.read()
            state.gain += (min(12, max(1, 0.8 / max(peak, 1e-4))) - state.gain) * 0.1
            state.trail.append(wave)
            if state.trail.count > 6 { state.trail.removeFirst() }
            // The phosphor: each older trace fainter, as the desktop's 32% fade per frame.
            for (age, w) in state.trail.reversed().enumerated().reversed() {
                glowStroke(tracePath(w, 1), opacity: pow(0.68, Double(age)))
            }
            if listener.handsFree {
                let quiet = listener.quietFor
                if quiet > Listener.showQuiet {
                    let k = max(0, (Listener.quietSeconds - quiet) / (Listener.quietSeconds - Listener.showQuiet))
                    ctx.fill(Path(CGRect(x: W / 2 - (W / 2 - 10) * k, y: H - 3, width: (W - 20) * k, height: 1.5)), with: .color(phos))
                }
            }
        case .settling:
            flat(0.35)
            let x = (e / 1.6).truncatingRemainder(dividingBy: max(W, 1))
            ctx.fill(Path(ellipseIn: CGRect(x: x - 2.5, y: mid - 2.5, width: 5, height: 5)), with: .color(phos))
        case .sent:
            // The TV power-off: the trace flattens, the line shrinks to a dot, the dot goes out.
            let p = min(1, e / 420)
            if p < 0.3, let last = state.trail.last {
                glowStroke(tracePath(last, Float(1 - p / 0.3)), opacity: 1)
            } else if p < 0.8 {
                let half = W / 2 * (1 - (p - 0.3) / 0.5)
                var l = Path(); l.move(to: CGPoint(x: W / 2 - half, y: mid)); l.addLine(to: CGPoint(x: W / 2 + half, y: mid))
                glowStroke(l, opacity: 0.75, width: 2.2)
            } else {
                let r = 3 * (1 - (p - 0.8) / 0.2)
                ctx.fill(Path(ellipseIn: CGRect(x: W / 2 - r, y: mid - r, width: 2 * r, height: 2 * r)), with: .color(phos))
            }
        case .note:
            flat(0.35)
        case .idle:
            break
        }
    }
}
