using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Xunit;

namespace Perch.Tests;

/// Dictation's host half: the clip the page sends is read back as the samples
/// it recorded, Whisper's non-speech tags never reach a prompt, and a clip too
/// short to be speech is answered without touching the model (so a slip of
/// the finger never starts a 140 MB download).
public class VoiceControllerTests
{
    [Fact]
    public void Pcm16_decodes_little_endian_into_unit_floats()
    {
        var bytes = new byte[] { 0xFF, 0x7F, 0x00, 0x80, 0x00, 0x00 };   // 32767, -32768, 0
        var s = VoiceController.DecodePcm16(bytes);
        Assert.Equal(3, s.Length);
        Assert.Equal(32767 / 32768f, s[0]);
        Assert.Equal(-1f, s[1]);
        Assert.Equal(0f, s[2]);
    }

    [Fact]
    public void Pcm16_is_capped_at_the_longest_clip()
    {
        var bytes = new byte[(VoiceController.MaxSeconds + 5) * VoiceController.SampleRate * 2];
        Assert.Equal(VoiceController.MaxSeconds * VoiceController.SampleRate, VoiceController.DecodePcm16(bytes).Length);
    }

    [Theory]
    [InlineData(" [BLANK_AUDIO]", "")]
    [InlineData(" (music) ", "")]
    [InlineData(" Make the card taller. [MUSIC] And commit it.", "Make the card taller. And commit it.")]
    [InlineData(" *coughs* ok push it", "ok push it")]
    [InlineData("  move the pane (the left one) up", "move the pane (the left one) up")]
    public void Clean_drops_non_speech_tags_and_keeps_what_was_said(string raw, string want)
    {
        Assert.Equal(want, VoiceController.Clean(raw));
    }

    [Fact]
    public void A_clip_too_short_to_be_speech_is_answered_empty_without_the_model()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-voice-" + Guid.NewGuid().ToString("N"));
        var posted = new List<string>();
        using var done = new ManualResetEventSlim();
        using var voice = new VoiceController(p => { lock (posted) posted.Add(JsonSerializer.Serialize(p)); done.Set(); }, dir);

        voice.Transcribe("r1", Convert.ToBase64String(new byte[VoiceController.SampleRate / 10 * 2]));   // 0.1 s

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.Single(posted);
        Assert.Contains("\"type\":\"voice.result\"", posted[0]);
        Assert.Contains("\"reqId\":\"r1\"", posted[0]);
        Assert.Contains("\"text\":\"\"", posted[0]);
        Assert.False(Directory.Exists(dir));   // nothing fetched
    }
}

/// The real thing, run by hand: fetch the model (when not already there) and
/// transcribe a spoken WAV. Opt-in — it downloads ~142 MB the first time.
///   PERCH_VOICE_LIVE_WAV=<16 kHz mono PCM16 .wav>  PERCH_VOICE_LIVE_DIR=<model dir>
///   dotnet test src/Perch.Tests -f net8.0 --filter VoiceLiveTests
public class VoiceLiveTests
{
    [Fact]
    public void A_spoken_clip_comes_back_as_its_words()
    {
        var wav = Environment.GetEnvironmentVariable("PERCH_VOICE_LIVE_WAV");
        var dir = Environment.GetEnvironmentVariable("PERCH_VOICE_LIVE_DIR");
        if (string.IsNullOrEmpty(wav) || string.IsNullOrEmpty(dir)) return;   // opt-in
        var pcm = File.ReadAllBytes(wav)[44..];   // canonical 44-byte WAV header
        string? result = null;
        using var done = new ManualResetEventSlim();
        using var voice = new VoiceController(p =>
        {
            var json = JsonSerializer.Serialize(p);
            if (json.Contains("\"voice.result\"")) { result = json; done.Set(); }
        }, dir);

        voice.Transcribe("live", Convert.ToBase64String(pcm));

        Assert.True(done.Wait(TimeSpan.FromMinutes(10)), "no result");
        var text = JsonDocument.Parse(result!).RootElement.GetProperty("text").GetString() ?? "";
        Console.WriteLine($"VOICE_LIVE: {text}");
        Assert.Contains("board", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("commit", text, StringComparison.OrdinalIgnoreCase);
    }
}
