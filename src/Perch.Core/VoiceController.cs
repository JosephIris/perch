using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace Perch;

/// Dictation: turns a clip the page recorded into text with Whisper, run on
/// this machine. The page owns the microphone, the scope and where the text
/// goes; this only owns the model and the words.
///
/// The model (whisper.cpp's ggml base.en, ~142 MB) is not shipped: it
/// downloads once, on the first press of the mic, into <data>/perch/voice.
/// A clip recorded while it downloads waits for it rather than failing.
/// Audio never leaves the machine; the download is the only network use.
internal sealed class VoiceController : IDisposable
{
    internal const string ModelName = "ggml-base.en.bin";
    internal const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";
    internal const int SampleRate = 16000;
    /// Longest clip transcribed; the page stops recording well before this.
    internal const int MaxSeconds = 300;
    /// Words Whisper would otherwise spell like a dictionary: a prompt biases
    /// it toward how they're written in this app.
    private const string Vocabulary = "Perch, Claude, Codex, repo, commit, push, pane, tab, thread, board, worktree, PR.";

    /// Posts a page message; the caller marshals onto the UI thread.
    private readonly Action<object> _post;
    private readonly string _dir;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<string>? _download;
    private WhisperFactory? _factory;
    private int _lastPct = -1;

    public VoiceController(Action<object> post, string? dir = null)
    {
        _post = post;
        _dir = dir ?? Path.Combine(AppPaths.DataRoot, "perch", "voice");
    }

    private string ModelPath => Path.Combine(_dir, ModelName);

    /// The page's mic was pressed: say where the model stands, and start
    /// fetching it if it isn't here yet.
    public void Prepare()
    {
        if (File.Exists(ModelPath)) { _post(new { type = "voice.model", state = "ready" }); return; }
        _ = EnsureModelAsync().ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private Task<string> EnsureModelAsync()
    {
        if (File.Exists(ModelPath)) return Task.FromResult(ModelPath);
        lock (_lock) return _download ??= DownloadAsync();
    }

    private async Task<string> DownloadAsync()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var part = ModelPath + ".part";
            _post(new { type = "voice.model", state = "downloading", pct = 0 });
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var resp = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            await using (var dst = File.Create(part))
            {
                var buf = new byte[1 << 16];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                    got += n;
                    var pct = total > 0 ? (int)(got * 100 / total) : 0;
                    if (pct != _lastPct) { _lastPct = pct; _post(new { type = "voice.model", state = "downloading", pct }); }
                }
            }
            File.Move(part, ModelPath, overwrite: true);
            Log.Info("Voice.model", $"downloaded {ModelName} to {_dir}");
            _post(new { type = "voice.model", state = "ready" });
            return ModelPath;
        }
        catch (Exception ex)
        {
            Log.Error("Voice.download", ex);
            // Let the next press try again.
            lock (_lock) _download = null;
            _lastPct = -1;
            _post(new { type = "voice.model", state = "error", error = ex.Message });
            throw;
        }
    }

    /// Transcribe one clip (16 kHz mono PCM16, little-endian, base64) and
    /// post `voice.result` with the same request id — empty text when
    /// nothing was said, plus `error` when it failed.
    public async void Transcribe(string reqId, string b64)
    {
        try
        {
            var samples = DecodePcm16(Convert.FromBase64String(b64));
            if (samples.Length < SampleRate / 4) { _post(new { type = "voice.result", reqId, text = "" }); return; }
            var path = await EnsureModelAsync().ConfigureAwait(false);
            var text = await Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    _factory ??= WhisperFactory.FromPath(path);
                    using var proc = _factory.CreateBuilder()
                        .WithLanguage("en")
                        .WithPrompt(Vocabulary)
                        .Build();
                    var sb = new StringBuilder();
                    await foreach (var seg in proc.ProcessAsync(samples).ConfigureAwait(false)) sb.Append(seg.Text);
                    return Clean(sb.ToString());
                }
                finally { _gate.Release(); }
            }).ConfigureAwait(false);
            Log.Info("Voice.result", $"req={reqId} seconds={samples.Length / (double)SampleRate:0.0} chars={text.Length}");
            _post(new { type = "voice.result", reqId, text });
        }
        catch (Exception ex)
        {
            Log.Error("Voice.transcribe", ex);
            _post(new { type = "voice.result", reqId, text = "", error = ex.Message });
        }
    }

    /// PCM16 little-endian → floats in [-1, 1], capped at MaxSeconds. Pure.
    internal static float[] DecodePcm16(byte[] bytes)
    {
        var n = Math.Min(bytes.Length / 2, SampleRate * MaxSeconds);
        var samples = new float[n];
        for (var i = 0; i < n; i++) samples[i] = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8)) / 32768f;
        return samples;
    }

    private static readonly Regex Noise = new(
        @"\[[^\]]*\]|\(\s*(?:music|inaudible|silence|laughs?|laughter|applause|coughs?|sighs?|static|noise|beeps?|clears throat)\s*\)|\*[^*]*\*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// Whisper's transcript without its non-speech tags ("[BLANK_AUDIO]",
    /// "(music)", "*coughs*") and with its segments' leading spaces folded
    /// into single spaces. Pure.
    internal static string Clean(string raw)
    {
        var s = Noise.Replace(raw ?? "", " ");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }
}
