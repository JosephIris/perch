// How things move: soft springs (no bounce you'd notice), a gentle press on
// buttons, messages that rise into place, and Claude "typing". Everything
// here stands still when Reduce Motion is on.

import SwiftUI

enum Motion {
    /// Things arriving or rearranging.
    static let soft = Animation.spring(response: 0.42, dampingFraction: 0.86)
    /// Small, quick responses to a touch.
    static let quick = Animation.spring(response: 0.26, dampingFraction: 0.82)
}

extension View {
    /// `.animation`, but none when Reduce Motion is on.
    func motion<V: Equatable>(_ animation: Animation, value: V) -> some View {
        modifier(MotionModifier(animation: animation, value: value))
    }

    /// A message rising softly into place.
    func arrives() -> some View {
        transition(.asymmetric(
            insertion: .opacity.combined(with: .offset(y: 14)).combined(with: .scale(scale: 0.98, anchor: .bottom)),
            removal: .opacity))
    }
}

private struct MotionModifier<V: Equatable>: ViewModifier {
    let animation: Animation
    let value: V
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    func body(content: Content) -> some View {
        content.animation(reduceMotion ? nil : animation, value: value)
    }
}

/// Pressed buttons sink a little and dim, then spring back.
struct PressableStyle: ButtonStyle {
    var scale: CGFloat = 0.92
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? scale : 1)
            .opacity(configuration.isPressed ? 0.85 : 1)
            .animation(Motion.quick, value: configuration.isPressed)
    }
}

/// Three dots breathing in turn, in a small bubble: Claude is working.
struct TypingIndicator: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        TimelineView(.animation(minimumInterval: 1 / 30, paused: reduceMotion)) { tl in
            let t = tl.date.timeIntervalSinceReferenceDate
            HStack(spacing: 5) {
                ForEach(0..<3) { i in
                    let phase = reduceMotion ? 0.5 : Self.phase(t, dot: i)
                    Circle()
                        .fill(Color.secondary)
                        .frame(width: 7, height: 7)
                        .opacity(0.35 + 0.65 * phase)
                        .scaleEffect(0.8 + 0.25 * phase)
                }
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 12)
            .background(Color(.secondarySystemGroupedBackground), in: Capsule())
        }
        .accessibilityLabel("Claude is working")
    }

    /// 0…1, each dot a beat behind the one before, a 1.2 s cycle.
    private static func phase(_ t: Double, dot: Int) -> Double {
        let angle: Double = t * 2 * Double.pi / 1.2 - Double(dot) * 0.9
        return (sin(angle) + 1) / 2
    }
}
