using System.Diagnostics;
using System.Media;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Raphael;

/// <summary>
/// Speaks Japanese through a locally running VOICEVOX engine (http://127.0.0.1:50021).
/// Defaults to the ナースロボ＿タイプＴ (NurseRobo Type T) character, ノーマル style.
/// </summary>
public class VoicevoxClient : IDisposable
{
    private const string BaseUrl = "http://127.0.0.1:50021/";
    private const int DefaultSpeakerId = 47; // NurseRobo Type T / ノーマル

    // "ナースロボ＿タイプＴ" and "ノーマル" — escaped because the name uses fullwidth characters.
    private const string DefaultCharacter = "ナースロボ＿タイプＴ";
    private const string DefaultStyle = "ノーマル";

    private readonly HttpClient _http = new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
    private readonly object _playLock = new();
    private SoundPlayer? _player;
    private CancellationTokenSource? _speechCts;
    private Process? _engineProcess;
    private int _speakerId = DefaultSpeakerId;

    public bool IsAvailable { get; private set; }

    /// <summary>Raised with the WAV bytes each time a clip starts playing.</summary>
    public event Action<byte[]>? AudioStarted;

    /// <summary>
    /// Makes sure the engine is up (launching it if needed) and resolves the speaker ID by name
    /// (falls back to the default ID).
    /// </summary>
    public async Task<bool> InitAsync(string characterName = DefaultCharacter, string styleName = DefaultStyle)
    {
        try
        {
            if (!await PingAsync() && !await LaunchEngineAsync())
                return false;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var raw = await _http.GetStringAsync("speakers", cts.Token);

            foreach (var speaker in JsonNode.Parse(raw)!.AsArray())
            {
                if (speaker!["name"]!.GetValue<string>() != characterName) continue;
                foreach (var style in speaker["styles"]!.AsArray())
                {
                    if (style!["name"]!.GetValue<string>() == styleName)
                        _speakerId = style["id"]!.GetValue<int>();
                }
            }

            IsAvailable = true;

            // The first synthesis after the engine starts is the slowest: get it out of the way now.
            _ = Task.Run(async () => { try { await SynthesizeAsync("はい。", CancellationToken.None); } catch { } });
        }
        catch
        {
            IsAvailable = false;
        }
        return IsAvailable;
    }

    private async Task<bool> PingAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var resp = await _http.GetAsync("version", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts VOICEVOX's engine in the background and waits for it to answer.</summary>
    private async Task<bool> LaunchEngineAsync()
    {
        var exe = FindVoicevoxExecutable();
        if (exe == null)
        {
            Console.WriteLine("VOICEVOX install not found. Set VOICEVOX_PATH to VOICEVOX.exe (or vv-engine\\run.exe), or start it manually.");
            return false;
        }

        Console.WriteLine($"Starting VOICEVOX ({exe})...");
        try
        {
            _engineProcess = Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not start VOICEVOX: {ex.Message}");
            return false;
        }

        // The first launch loads the voice models, so give it a while.
        for (var i = 0; i < 60; i++)
        {
            if (await PingAsync()) return true;
            if (_engineProcess is { HasExited: true }) break;
            await Task.Delay(1000);
        }

        Console.WriteLine("VOICEVOX didn't become ready in time.");
        return false;
    }

    /// <summary>
    /// Looks for the headless engine first (lighter, no window), then the full app:
    /// VOICEVOX_PATH, a "VOICEVOX" folder beside the project, then the default install folders.
    /// </summary>
    private static string? FindVoicevoxExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("VOICEVOX_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        var roots = new List<string>();
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            roots.Add(Path.Combine(dir.FullName, "VOICEVOX"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VOICEVOX"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VOICEVOX"));

        foreach (var root in roots)
        {
            foreach (var relative in new[] { @"vv-engine\run.exe", "VOICEVOX.exe" })
            {
                var candidate = Path.Combine(root, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Speaks the text one sentence at a time: the first sentence starts playing as soon as it has been
    /// generated, while the rest are generated in the background. A newer call cuts off whatever is still
    /// playing. Completes when the speech has finished (or was cut off).
    /// </summary>
    /// <param name="onFirstAudio">Called just before the first sentence is heard (used to show the subtitle in sync).</param>
    public async Task SpeakAsync(string japaneseText, Action? onFirstAudio = null)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(japaneseText)) return;

        CancellationTokenSource cts;
        lock (_playLock)
        {
            _speechCts?.Cancel();
            _player?.Stop(); // cut the previous speech off immediately
            cts = _speechCts = new CancellationTokenSource();
        }
        var ct = cts.Token;

        var ready = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

        // Producer: generate the pieces in order, staying ahead of playback.
        var producer = Task.Run(async () =>
        {
            try
            {
                var body = japaneseText.Trim();
                var hasMarker = body.StartsWith("告。");
                if (hasMarker)
                {
                    // Her "Notice." marker is spoken on its own, louder and sharper, with a beat of silence after it.
                    body = body[2..].Trim();
                    await ready.Writer.WriteAsync(await SynthesizeMarkerAsync(ct), ct);
                }

                foreach (var piece in SplitIntoPieces(body))
                {
                    ct.ThrowIfCancellationRequested();
                    // After the marker the sentence is a little quieter, so the marker stands out.
                    var wav = await SynthesizeAsync(piece, ct, hasMarker ? 1.2 : 1.5);
                    await ready.Writer.WriteAsync(wav, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[voicevox] {ex.Message}"); }
            finally { ready.Writer.TryComplete(); }
        });

        var started = false;
        try
        {
            await foreach (var wav in ready.Reader.ReadAllAsync(ct))
            {
                if (!started)
                {
                    started = true;
                    onFirstAudio?.Invoke();
                }
                await PlayAsync(wav, ct);
            }

            // Nothing could be generated: still show the subtitle rather than leaving the reply silent and unseen.
            if (!started && !ct.IsCancellationRequested) onFirstAudio?.Invoke();
        }
        catch (OperationCanceledException) { }
        finally
        {
            await producer;
            lock (_playLock)
            {
                if (ReferenceEquals(_speechCts, cts)) _speechCts = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>Plays one clip and returns when it has finished (or was stopped).</summary>
    private async Task PlayAsync(byte[] wav, CancellationToken ct)
    {
        using var player = new SoundPlayer(new MemoryStream(wav));
        lock (_playLock)
        {
            if (ct.IsCancellationRequested) return;
            _player = player;
        }

        AudioStarted?.Invoke(wav);

        using var stopOnCancel = ct.Register(() =>
        {
            try { player.Stop(); } catch { /* already finished */ }
        });

        try
        {
            await Task.Run(player.PlaySync); // returns as soon as the clip ends, so the next one follows without a gap
        }
        finally
        {
            lock (_playLock)
            {
                if (ReferenceEquals(_player, player)) _player = null;
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    // The marker's loudness boost and the silence after it (chosen by ear).
    private const double MarkerGain = 2.2;
    private const double MarkerPauseSeconds = 0.8;

    /// <summary>
    /// "告。" as "koku": accent on the last mora, strong intonation, lower pitch, then boosted through a soft limiter
    /// (louder without harsh clipping) and followed by a pause.
    /// </summary>
    private async Task<byte[]> SynthesizeMarkerAsync(CancellationToken ct)
    {
        using var queryResp = await _http.PostAsync(
            $"audio_query?speaker={_speakerId}&is_kana=true&text={Uri.EscapeDataString("コク'")}", null, ct);
        queryResp.EnsureSuccessStatusCode();
        var query = JsonNode.Parse(await queryResp.Content.ReadAsStringAsync(ct))!;
        query["speedScale"] = 0.95;
        query["intonationScale"] = 1.7;
        query["pitchScale"] = -0.05;
        query["volumeScale"] = 1.6;
        query["prePhonemeLength"] = 0.05;
        query["postPhonemeLength"] = 0.05;

        using var body = new StringContent(query.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        using var wavResp = await _http.PostAsync($"synthesis?speaker={_speakerId}", body, ct);
        wavResp.EnsureSuccessStatusCode();
        var wav = await wavResp.Content.ReadAsByteArrayAsync(ct);
        return BoostAndPad(wav, MarkerGain, MarkerPauseSeconds);
    }

    /// <summary>Louder (tanh soft limiter) and with silence appended, for a standard 44-byte-header 16-bit mono WAV.</summary>
    private static byte[] BoostAndPad(byte[] wav, double gain, double padSeconds)
    {
        const int header = 44;
        if (wav.Length <= header) return wav;

        var sampleRate = BitConverter.ToInt32(wav, 24);
        var samples = (wav.Length - header) / 2;
        var padSamples = (int)(sampleRate * padSeconds);
        var result = new byte[header + 2 * (samples + padSamples)];
        Array.Copy(wav, result, header);

        for (var i = 0; i < samples; i++)
        {
            var x = BitConverter.ToInt16(wav, header + 2 * i) / 32768.0;
            var y = (short)(Math.Tanh(x * gain) * 0.95 * 32767);
            BitConverter.GetBytes(y).CopyTo(result, header + 2 * i);
        }

        BitConverter.GetBytes(result.Length - 8).CopyTo(result, 4);
        BitConverter.GetBytes(2 * (samples + padSamples)).CopyTo(result, 40);
        return result;
    }

    private async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct, double volume = 1.5)
    {
        // Read on its own, 告 comes out as "tsuge"; as her "Report." marker it should be "koku".
        text = Regex.Replace(text, @"^告(?=。)", "こく");

        using var queryResp = await _http.PostAsync(
            $"audio_query?speaker={_speakerId}&text={Uri.EscapeDataString(text)}", null, ct);
        queryResp.EnsureSuccessStatusCode();
        var query = JsonNode.Parse(await queryResp.Content.ReadAsStringAsync(ct))!;

        // Flat, measured delivery.
        query["speedScale"] = 0.95;
        query["intonationScale"] = 0.6;
        query["pitchScale"] = -0.02;
        query["volumeScale"] = volume; // 1.0 = default; much above ~1.6 can start to distort

        using var body = new StringContent(query.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        using var wavResp = await _http.PostAsync($"synthesis?speaker={_speakerId}", body, ct);
        wavResp.EnsureSuccessStatusCode();
        return await wavResp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Sentences (keeping their punctuation). A long sentence is broken at commas so its first words are
    /// heard sooner, and very short pieces are joined to the next so they aren't generated on their own.
    /// </summary>
    private static List<string> SplitIntoPieces(string text)
    {
        var pieces = new List<string>();
        foreach (var sentence in Regex.Split(text.Trim(), @"(?<=[。！？!?])|\n+").Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            if (sentence.Length <= 22) { pieces.Add(sentence); continue; }

            var current = "";
            foreach (var part in Regex.Split(sentence, @"(?<=、)").Where(p => p.Length > 0))
            {
                current += part;
                if (current.Length >= 10) { pieces.Add(current); current = ""; }
            }
            if (current.Length > 0)
            {
                if (pieces.Count > 0 && current.Length < 8) pieces[^1] += current;
                else pieces.Add(current);
            }
        }

        for (var i = 0; i < pieces.Count - 1; i++)
        {
            if (pieces[i].Length < 6)
            {
                pieces[i + 1] = pieces[i] + pieces[i + 1];
                pieces.RemoveAt(i);
                i--;
            }
        }
        return pieces;
    }

    public void Dispose()
    {
        _speechCts?.Cancel(); // playback owns and disposes its own player
        _http.Dispose();

        // Only shut down an engine that we started ourselves.
        try
        {
            if (_engineProcess is { HasExited: false })
                _engineProcess.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
        _engineProcess?.Dispose();
    }
}
