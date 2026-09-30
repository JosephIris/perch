// Speech on the phone: what you say becomes text here (on-device when the
// phone supports it) and only the text goes to Perch; answers are read back
// with the system voice.

import AVFoundation
import Observation
import Speech

@Observable @MainActor
final class Listener {
    private(set) var transcript = ""
    private(set) var isListening = false
    private(set) var problem: String?
    /// Tap-to-talk: stop by itself after a short pause and call `onPause`.
    var untilPause = false { didSet { if untilPause { armPauseTimer() } } }
    var onPause: (() -> Void)?

    private let recognizer = SFSpeechRecognizer()
    private let engine = AVAudioEngine()
    private var request: SFSpeechAudioBufferRecognitionRequest?
    private var task: SFSpeechRecognitionTask?
    private var gotFinal = false
    private var stopRequested = false
    private var pauseTimer: Task<Void, Never>?

    func start() async {
        guard !isListening else { return }
        transcript = ""
        problem = nil
        gotFinal = false
        stopRequested = false
        untilPause = false
        isListening = true
        guard await authorized() else { isListening = false; return }
        // Released before the permission prompts were answered.
        if stopRequested { isListening = false; return }
        guard let recognizer, recognizer.isAvailable else {
            problem = "Speech recognition isn't available right now."
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
            input.installTap(onBus: 0, bufferSize: 1024, format: format) { buffer, _ in req.append(buffer) }
            engine.prepare()
            try engine.start()

            task = recognizer.recognitionTask(with: req) { [weak self] result, error in
                let text = result?.bestTranscription.formattedString
                let final = result?.isFinal ?? false
                Task { @MainActor in self?.heard(text, final: final || error != nil) }
            }
        } catch {
            problem = "Couldn't start the microphone."
            teardown()
        }
    }

    /// Stops listening and returns what was heard, waiting briefly for the
    /// recognizer's final words.
    func stop() async -> String {
        stopRequested = true
        guard isListening else { return transcript }
        pauseTimer?.cancel()
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

    private func heard(_ text: String?, final: Bool) {
        if let text, !text.isEmpty, text != transcript {
            transcript = text
            if untilPause { armPauseTimer() }
        }
        if final { gotFinal = true }
    }

    /// Nothing new for a moment (or nothing at all for a while) ends a tap-to-talk.
    private func armPauseTimer() {
        pauseTimer?.cancel()
        let wait: Duration = transcript.isEmpty ? .seconds(8) : .milliseconds(1500)
        pauseTimer = Task { [weak self] in
            try? await Task.sleep(for: wait)
            guard !Task.isCancelled, let self, self.isListening, self.untilPause else { return }
            self.onPause?()
        }
    }

    private func stopAudio() {
        if engine.isRunning { engine.stop() }
        engine.inputNode.removeTap(onBus: 0)
    }

    private func teardown() {
        pauseTimer?.cancel()
        stopAudio()
        task?.cancel()
        task = nil
        request = nil
        isListening = false
        untilPause = false
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }

    private func authorized() async -> Bool {
        let speech = await withCheckedContinuation { c in
            SFSpeechRecognizer.requestAuthorization { c.resume(returning: $0) }
        }
        guard speech == .authorized else {
            problem = "Allow speech recognition for Perch in Settings to talk."
            return false
        }
        guard await AVAudioApplication.requestRecordPermission() else {
            problem = "Allow the microphone for Perch in Settings to talk."
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
