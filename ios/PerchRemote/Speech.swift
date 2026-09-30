// Speech on the phone: what you say becomes text here (on-device when the
// phone supports it) and only the text goes to Perch; answers are read back
// with the system voice. Hold the mic and let go to send; tap it and it sends
// after 2.5 s of quiet, as the desktop's dictation does (src/web/src/voice.ts).

import AVFoundation
import Observation
import Speech

/// The microphone's latest samples for the scope, written on the audio thread
/// and read by the scope every frame.
final class ScopeFeed: @unchecked Sendable {
    static let points = 256
    private let lock = NSLock()
    private var _wave = [Float](repeating: 0, count: ScopeFeed.points)
    private var _level: Float = 0
    private var _peak: Float = 0

    func read() -> (wave: [Float], level: Float, peak: Float) {
        lock.lock(); defer { lock.unlock() }
        return (_wave, _level, _peak)
    }

    #if DEBUG
    /// A synthetic voice for screenshots of the scope (no microphone in the Simulator).
    func writeDemo(at t: Double) {
        let env = Float(0.35 + 0.3 * sin(t * 2.3) * sin(t * 0.7))
        let wave = (0..<Self.points).map { i -> Float in
            let x = Double(i) / Double(Self.points)
            return env * Float(sin(x * 38 + t * 9) * 0.6 + sin(x * 97 + t * 23) * 0.3 + sin(x * 11 - t * 4) * 0.4)
        }
        lock.lock(); _wave = wave; _level = env; _peak = env; lock.unlock()
    }
    #endif

    func reset() {
        lock.lock(); defer { lock.unlock() }
        _wave = [Float](repeating: 0, count: Self.points); _level = 0; _peak = 0
    }

    /// Level 0–1 the way the desktop measures it: RMS × 7, eased by ^0.75.
    func write(_ buffer: AVAudioPCMBuffer) {
        guard let ch = buffer.floatChannelData?[0] else { return }
        let n = Int(buffer.frameLength)
        guard n > 0 else { return }
        let span = min(n, 1024), from = n - span
        var wave = [Float](repeating: 0, count: Self.points)
        var sum: Float = 0, peak: Float = 0
        for i in from..<n { let v = ch[i]; sum += v * v; peak = max(peak, abs(v)) }
        for i in 0..<Self.points { wave[i] = ch[from + i * span / Self.points] }
        let level = min(1, pow(sqrt(sum / Float(span)) * 7, 0.75))
        lock.lock(); _wave = wave; _level = level; _peak = peak; lock.unlock()
    }
}

@Observable @MainActor
final class Listener {
    /// Hands-free sends after this long quiet; the countdown shows after `showQuiet`.
    static let quietSeconds = 2.5
    static let showQuiet = 1.0
    static let quietLevel: Float = 0.15
    /// A recognition request runs about a minute; stop and send before that.
    static let maxSeconds = 55.0

    private(set) var transcript = ""
    private(set) var isListening = false
    /// Why the mic didn't start, in the scope's short capitals.
    private(set) var problem: String?
    /// Hands-free (a tap rather than a hold): ends by itself after a quiet spell.
    var handsFree = false { didSet { if handsFree { quietSince = Date() } } }
    /// Called when hands-free hears enough quiet, or the clip runs too long.
    var onDone: (() -> Void)?
    private(set) var startedAt = Date()
    @ObservationIgnored private(set) var quietSince = Date()

    @ObservationIgnored let feed = ScopeFeed()
    private let recognizer = SFSpeechRecognizer()
    private let engine = AVAudioEngine()
    private var request: SFSpeechAudioBufferRecognitionRequest?
    private var task: SFSpeechRecognitionTask?
    private var gotFinal = false
    private var stopRequested = false
    private var watch: Task<Void, Never>?

    func start() async {
        guard !isListening else { return }
        transcript = ""
        problem = nil
        gotFinal = false
        stopRequested = false
        handsFree = false
        feed.reset()
        startedAt = Date()
        isListening = true
        guard await authorized() else { isListening = false; return }
        // Released before the permission prompts were answered.
        if stopRequested { isListening = false; return }
        guard let recognizer, recognizer.isAvailable else {
            problem = "SPEECH RECOGNITION UNAVAILABLE"
            isListening = false
            return
        }
        do {
            let audio = AVAudioSession.sharedInstance()
            try audio.setCategory(.record, mode: .measurement, options: .duckOthers)
            try audio.setActive(true, options: .notifyOthersOnDeactivation)

            let req = SFSpeechAudioBufferRecognitionRequest()
            req.shouldReportPartialResults = true
            req.addsPunctuation = true
            if recognizer.supportsOnDeviceRecognition { req.requiresOnDeviceRecognition = true }
            request = req

            let input = engine.inputNode
            let format = input.outputFormat(forBus: 0)
            guard format.sampleRate > 0 else { throw ListenError.noMicrophone }
            let feed = self.feed
            input.installTap(onBus: 0, bufferSize: 1024, format: format) { buffer, _ in
                req.append(buffer)
                feed.write(buffer)
            }
            engine.prepare()
            try engine.start()
            startedAt = Date()
            quietSince = startedAt

            task = recognizer.recognitionTask(with: req) { [weak self] result, error in
                let text = result?.bestTranscription.formattedString
                let final = result?.isFinal ?? false
                Task { @MainActor in self?.heard(text, final: final || error != nil) }
            }
            watch = Task { [weak self] in await self?.watchLevels() }
        } catch {
            problem = "NO MICROPHONE ACCESS"
            teardown()
        }
    }

    /// Stops listening and returns what was heard, waiting briefly for the
    /// recognizer's final words.
    func stop() async -> String {
        stopRequested = true
        guard isListening else { return transcript }
        watch?.cancel()
        stopAudio()
        request?.endAudio()
        let deadline = Date().addingTimeInterval(1.5)
        while !gotFinal, task != nil, Date() < deadline {
            try? await Task.sleep(for: .milliseconds(100))
        }
        teardown()
        return transcript
    }

    func cancel() {
        stopRequested = true
        teardown()
        transcript = ""
    }

    /// How long it has been quiet, while hands-free.
    var quietFor: TimeInterval { handsFree ? Date().timeIntervalSince(quietSince) : 0 }

    private func heard(_ text: String?, final: Bool) {
        if let text, !text.isEmpty, text != transcript { transcript = text }
        if final { gotFinal = true }
    }

    /// Hands-free ends after a quiet spell; any clip ends before the
    /// recognizer's limit.
    private func watchLevels() async {
        while !Task.isCancelled, isListening {
            let now = Date()
            if feed.read().level > Self.quietLevel { quietSince = now }
            if now.timeIntervalSince(startedAt) > Self.maxSeconds
                || (handsFree && now.timeIntervalSince(quietSince) > Self.quietSeconds) {
                onDone?()
                return
            }
            try? await Task.sleep(for: .milliseconds(50))
        }
    }

    private func stopAudio() {
        if engine.isRunning { engine.stop() }
        engine.inputNode.removeTap(onBus: 0)
    }

    private func teardown() {
        watch?.cancel()
        stopAudio()
        task?.cancel()
        task = nil
        request = nil
        isListening = false
        handsFree = false
        feed.reset()
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }

    private func authorized() async -> Bool {
        let speech = await withCheckedContinuation { c in
            SFSpeechRecognizer.requestAuthorization { c.resume(returning: $0) }
        }
        guard speech == .authorized else {
            problem = "SPEECH RECOGNITION IS OFF IN SETTINGS"
            return false
        }
        guard await AVAudioApplication.requestRecordPermission() else {
            problem = "NO MICROPHONE ACCESS"
            return false
        }
        return true
    }

    private enum ListenError: Error { case noMicrophone }
}

@Observable @MainActor
final class Speaker: NSObject, AVSpeechSynthesizerDelegate {
    private(set) var isSpeaking = false
    private let synth = AVSpeechSynthesizer()

    override init() {
        super.init()
        synth.delegate = self
    }

    func speak(_ text: String) {
        guard !text.isEmpty else { return }
        synth.stopSpeaking(at: .immediate)
        let audio = AVAudioSession.sharedInstance()
        try? audio.setCategory(.playback, mode: .spokenAudio, options: .duckOthers)
        try? audio.setActive(true)
        let u = AVSpeechUtterance(string: text)
        u.voice = AVSpeechSynthesisVoice(language: AVSpeechSynthesisVoice.currentLanguageCode())
        synth.speak(u)
        isSpeaking = true
    }

    func stop() {
        synth.stopSpeaking(at: .immediate)
        isSpeaking = false
    }

    nonisolated func speechSynthesizer(_ s: AVSpeechSynthesizer, didFinish u: AVSpeechUtterance) {
        Task { @MainActor in self.finished() }
    }

    nonisolated func speechSynthesizer(_ s: AVSpeechSynthesizer, didCancel u: AVSpeechUtterance) {
        Task { @MainActor in self.finished() }
    }

    private func finished() {
        guard !synth.isSpeaking else { return }
        isSpeaking = false
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }
}
