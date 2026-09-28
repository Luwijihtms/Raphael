using System.Globalization;
using System.Speech.Recognition;
using System.Text.RegularExpressions;
using System.Speech.Synthesis;

namespace Raphael;

/// <summary>
/// Handles listening (wake word + command capture) and speaking (TTS)
/// using Windows' built-in, fully offline speech engine.
/// Tuned to sound flat / analytical — "Raphael" style — rather than chipper.
/// </summary>
public class VoiceService : IDisposable
{
    private readonly SpeechRecognitionEngine? _recognizer; // null when the microphone is switched off
    private readonly SpeechSynthesizer _synth;
    private readonly Regex _wakeWordPattern;
    private readonly VoicevoxClient _voicevox = new();
    private readonly SubtitleOverlay _overlay = new();
    private readonly OrbOverlay _orb = new();

    private static readonly TimeSpan CommandWindow = TimeSpan.FromSeconds(10);

    // What she says when called by name alone; picked at random so it isn't always the same.
    private static readonly (string Ja, string En)[] Acknowledgements =
    {
        ("はい。", "Yes."),
        ("お呼びでしょうか。", "You called?"),
        ("ご用件をどうぞ。", "Your instructions, please."),
        ("聞いています。", "I am listening."),
        ("何でしょうか。", "What is it?"),
    };
    private readonly object _stateLock = new();
    private DateTime _commandWindowFrom = DateTime.MinValue;
    private DateTime _commandWindowUntil = DateTime.MinValue;

    /// <summary>The command text as Windows guessed it (often wrong), plus the raw audio for a better transcription.</summary>
    public event Action<string, byte[]?>? OnCommandHeard;

    /// <param name="listenToMic">False = no microphone input at all (typed commands only); speech output still works.</param>
    public VoiceService(bool listenToMic, params string[] wakeWords)
    {
        _voicevox.AudioStarted += _orb.Speak;

        // Whole-word match (so "sage" doesn't fire inside "message"); longest first so
        // "great sage" is consumed as one wake word rather than leaving "great" behind.
        var alternatives = wakeWords.OrderByDescending(w => w.Length).Select(Regex.Escape);
        _wakeWordPattern = new Regex($@"\b(?:{string.Join("|", alternatives)})\b", RegexOptions.IgnoreCase);

        // --- Speech recognition setup ---
        if (listenToMic)
        {
            // Commands are spoken in English, so ask for the English recognizer explicitly
            // (the default follows Windows' display language, which may be Japanese).
            try { _recognizer = new SpeechRecognitionEngine(new CultureInfo("en-US")); }
            catch { _recognizer = new SpeechRecognitionEngine(); }
            _recognizer.SetInputToDefaultAudioDevice();
            Console.WriteLine($"Speech recognizer: {_recognizer.RecognizerInfo.Name} ({_recognizer.RecognizerInfo.Culture})");

            // Dictation grammar so it can hear arbitrary commands, not just fixed phrases.
            // On its own it is bad at proper nouns: it would turn "Raphael" into random words.
            _recognizer.LoadGrammar(new DictationGrammar());

            // Wake-word grammar: an explicit list of names, optionally followed by free dictation.
            // This is what makes "Raphael-san, ..." reliably recognised.
            var names = new Choices(wakeWords.Select(w => w.Replace('-', ' ')).Distinct().ToArray());
            var afterName = new GrammarBuilder();
            afterName.AppendDictation();
            var wakeGrammar = new GrammarBuilder(names);
            wakeGrammar.Append(afterName, 0, 1);
            _recognizer.LoadGrammar(new Grammar(wakeGrammar) { Name = "wake" });

            _recognizer.SpeechRecognitionRejected += (s, e) => Console.WriteLine("  [mic] (couldn't make that out)");

            // Helps diagnose a quiet or wrong microphone; only prints when the problem changes.
            var lastProblem = AudioSignalProblem.None;
            _recognizer.AudioSignalProblemOccurred += (s, e) =>
            {
                if (e.AudioSignalProblem == lastProblem) return;
                lastProblem = e.AudioSignalProblem;
                Console.WriteLine($"  [mic] audio problem: {e.AudioSignalProblem}");
            };

            _recognizer.SpeechRecognized += (s, e) =>
            {
                var text = e.Result.Text?.Trim() ?? "";
                if (text.Length == 0) return;
                Console.WriteLine($"  [mic] \"{text}\" (confidence {e.Result.Confidence:0.00})");

                var audioStart = e.Result.Audio?.StartTime ?? DateTime.Now;
                var match = _wakeWordPattern.Match(text);
                string? command = null;
                var wakeWordOnly = false;

                if (match.Success)
                {
                    // Strip the wake word out, keep whatever command followed it.
                    var afterWake = text[(match.Index + match.Length)..].Trim(' ', ',', '.', '-');
                    if (afterWake.Length > 0) command = afterWake;
                    else wakeWordOnly = true;
                }
                else
                {
                    // No wake word: it's a command only if it starts inside the window that
                    // opened after we finished saying "Yes?" (audio from before that is our own echo).
                    lock (_stateLock)
                    {
                        if (audioStart >= _commandWindowFrom && audioStart <= _commandWindowUntil)
                            command = text;
                    }
                }

                if (command != null)
                {
                    lock (_stateLock) _commandWindowUntil = DateTime.MinValue;
                    _orb.Listen(DateTime.MinValue);
                    OnCommandHeard?.Invoke(command, CaptureAudio(e.Result));
                }
                else if (wakeWordOnly)
                {
                    _ = AcknowledgeAsync();
                }
            };
        }

        // --- Speech synthesis setup ---
        _synth = new SpeechSynthesizer();
        _synth.SetOutputToDefaultAudioDevice();

        // Flatten the delivery: slightly slower, slightly lower pitch, minimal variance.
        _synth.Rate = -1;   // -10..10, slightly slower than default reads as more "analytical"
        _synth.Volume = 100;

        TrySelectNeutralVoice();
    }

    private void TrySelectNeutralVoice()
    {
        // Prefer any installed voice explicitly flagged neutral/male-adjacent flat tone if present;
        // otherwise fall back to the system default. Swap VoiceName below if you install
        // additional SAPI5 voices (e.g. via Windows Settings > Speech) and want a specific one.
        try
        {
            var voices = _synth.GetInstalledVoices();
            var preferred = voices.FirstOrDefault(v =>
                v.Enabled && v.VoiceInfo.Name.Contains("David", StringComparison.OrdinalIgnoreCase));
            if (preferred != null)
                _synth.SelectVoice(preferred.VoiceInfo.Name);
        }
        catch
        {
            // Fall back silently to default voice if selection fails.
        }
    }

    public Task<bool> InitJapaneseVoiceAsync() => _voicevox.InitAsync();
    /// <summary>The utterance as a WAV file, louder if the microphone level is low. Null if unavailable.</summary>
    private static byte[]? CaptureAudio(RecognitionResult result)
    {
        try
        {
            if (result.Audio == null) return null;
            using var ms = new MemoryStream();
            result.Audio.WriteToWaveStream(ms);
            return Normalize(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Scales 16-bit PCM so its peak reaches ~90% of full scale (at most 8x), helping quiet microphones.</summary>
    private static byte[] Normalize(byte[] wav)
    {
        // Find the "data" chunk instead of assuming a 44-byte header.
        var dataAt = -1;
        for (var i = 12; i + 8 <= wav.Length; i++)
        {
            if (wav[i] == 'd' && wav[i + 1] == 'a' && wav[i + 2] == 't' && wav[i + 3] == 'a') { dataAt = i + 8; break; }
        }
        if (dataAt < 0 || BitConverter.ToInt16(wav, 34) != 16) return wav;

        var peak = 0;
        for (var i = dataAt; i + 1 < wav.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wav, i)));
        if (peak < 200) return wav; // essentially silence: don't boost noise

        var gain = Math.Min(8.0, 0.9 * 32767 / peak);
        if (gain < 1.2) return wav;

        var boosted = (byte[])wav.Clone();
        for (var i = dataAt; i + 1 < boosted.Length; i += 2)
        {
            var s = (short)Math.Clamp((int)(BitConverter.ToInt16(boosted, i) * gain), short.MinValue, short.MaxValue);
            boosted[i] = (byte)(s & 0xFF);
            boosted[i + 1] = (byte)((s >> 8) & 0xFF);
        }
        return boosted;
    }

    /// <summary>
    /// Speaks the Japanese line with VOICEVOX and shows the English as a subtitle.
    /// If VOICEVOX isn't running, speaks the English line with the Windows voice instead.
    /// </summary>
    public void Say(string japanese, string english) => _ = SayAsync(japanese, english);

    /// <summary>Like <see cref="Say"/>, but the task completes once the speech has finished.</summary>
    public Task SayAsync(string japanese, string english)
    {
        void ShowSubtitle()
        {
            if (string.IsNullOrWhiteSpace(english)) return;
            Console.WriteLine($"  [subtitle] {english}");
            _overlay.Show(english);
        }

        // With the Japanese voice, the subtitle appears when she starts speaking, not while the audio is still being made.
        if (_voicevox.IsAvailable && !string.IsNullOrWhiteSpace(japanese))
            return _voicevox.SpeakAsync(japanese, ShowSubtitle);

        ShowSubtitle();
        Speak(english);
        var estimate = TimeSpan.FromMilliseconds(Math.Max(1500, english.Length * 65)); // rough spoken length
        _orb.SpeakSynthetic(estimate);
        return Task.Delay(estimate);
    }

    /// <summary>Shows the orb's "thinking" shimmer while a reply is being prepared.</summary>
    public void SetThinking(bool on) => _orb.SetThinking(on);

    /// <summary>
    /// Wake word said on its own: answer "Yes?", then treat the next thing you say
    /// (within <see cref="CommandWindow"/>) as the command, without needing the wake word again.
    /// </summary>
    private async Task AcknowledgeAsync()
    {
        lock (_stateLock) _commandWindowUntil = DateTime.MinValue; // not listening while we speak

        var (ja, en) = Acknowledgements[Random.Shared.Next(Acknowledgements.Length)];
        await SayAsync(ja, en);

        lock (_stateLock)
        {
            _commandWindowFrom = DateTime.Now;
            _commandWindowUntil = _commandWindowFrom + CommandWindow;
        }
        _orb.Listen(_commandWindowUntil);
        Console.WriteLine("  [listening for your command...]");
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _synth.SpeakAsyncCancelAll();
        _synth.SpeakAsync(text);
    }

    public void StartListening() => _recognizer?.RecognizeAsync(RecognizeMode.Multiple);

    public void StopListening() => _recognizer?.RecognizeAsyncStop();

    public void Dispose()
    {
        _recognizer?.Dispose();
        _synth.Dispose();
        _voicevox.Dispose();
        _overlay.Dispose();
        _orb.Dispose();
    }
}
