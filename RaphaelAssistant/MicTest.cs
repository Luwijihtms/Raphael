using System.Globalization;
using System.Speech.Recognition;

namespace Raphael;

/// <summary>
/// "Raphael.exe --mic-test": shows a live input-level meter and what the speech recognizer hears,
/// with no Gemini/Spotify/VOICEVOX involved. For diagnosing a silent or wrong microphone.
/// </summary>
public static class MicTest
{
    private static readonly object ConsoleLock = new();

    public static void Run(string[] wakeWords)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== Raphael microphone test ===");
        Console.WriteLine("Talk normally, about an arm's length from the mic.");
        Console.WriteLine("Try: \"Raphael-san, what time is it\".  Press Enter to stop.");
        Console.WriteLine();

        SpeechRecognitionEngine engine;
        try { engine = new SpeechRecognitionEngine(new CultureInfo("en-US")); }
        catch { engine = new SpeechRecognitionEngine(); }

        using (engine)
        {
            try
            {
                engine.SetInputToDefaultAudioDevice();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not open the default microphone: {ex.Message}");
                Console.WriteLine("Check Settings > Privacy & security > Microphone (allow desktop apps),");
                Console.WriteLine("and Settings > System > Sound > Input.");
                return;
            }

            Console.WriteLine($"Recognizer: {engine.RecognizerInfo.Name} ({engine.RecognizerInfo.Culture})");
            Console.WriteLine();

            // The same grammars the assistant uses.
            engine.LoadGrammar(new DictationGrammar());
            var names = new Choices(wakeWords.Select(w => w.Replace('-', ' ')).Distinct().ToArray());
            var afterName = new GrammarBuilder();
            afterName.AppendDictation();
            var wake = new GrammarBuilder(names);
            wake.Append(afterName, 0, 1);
            engine.LoadGrammar(new Grammar(wake) { Name = "wake" });

            int peak = 0, recognized = 0, wakeHits = 0, unclear = 0;
            var lastDraw = DateTime.MinValue;
            var lastProblem = AudioSignalProblem.None;

            engine.AudioLevelUpdated += (s, e) =>
            {
                peak = Math.Max(peak, e.AudioLevel);
                var now = DateTime.Now;
                if ((now - lastDraw).TotalMilliseconds < 50) return;
                lastDraw = now;

                var filled = Math.Clamp(e.AudioLevel * 30 / 100, 0, 30);
                lock (ConsoleLock)
                    Console.Write($"\r  level [{new string('#', filled)}{new string('.', 30 - filled)}] {e.AudioLevel,3}   peak {peak,3}   ");
            };

            engine.AudioSignalProblemOccurred += (s, e) =>
            {
                if (e.AudioSignalProblem == lastProblem) return;
                lastProblem = e.AudioSignalProblem;
                Print($"  ! audio problem: {e.AudioSignalProblem}");
            };

            engine.SpeechRecognized += (s, e) =>
            {
                recognized++;
                var text = e.Result.Text ?? "";
                var lower = text.ToLowerInvariant();
                var isWake = wakeWords.Any(w => lower.Contains(w.Replace('-', ' ').ToLowerInvariant()));
                if (isWake) wakeHits++;
                Print($"  heard: \"{text}\"  (confidence {e.Result.Confidence:0.00}){(isWake ? "   <- wake word detected" : "")}");
            };

            engine.SpeechRecognitionRejected += (s, e) =>
            {
                unclear++;
                Print("  heard something, but couldn't make it out");
            };

            engine.RecognizeAsync(RecognizeMode.Multiple);
            Console.ReadLine();
            engine.RecognizeAsyncCancel();

            Console.WriteLine();
            Console.WriteLine("--- Summary ---");
            Console.WriteLine($"Loudest level: {peak}/100   recognised: {recognized}   wake word heard: {wakeHits}   unclear: {unclear}");

            if (peak == 0)
                Console.WriteLine("No signal at all. Check Settings > Privacy & security > Microphone (allow desktop apps), and that the right device is the default input.");
            else if (peak < 15)
                Console.WriteLine("The mic is very quiet. Raise the input volume in Settings > System > Sound > Input, or move closer.");
            else if (recognized == 0)
                Console.WriteLine("Sound is arriving, but nothing was recognised. Speak more clearly and closer to the mic, in English.");
            else if (wakeHits == 0)
                Console.WriteLine("She hears you, but never caught her name. Say \"Raphael-san\" clearly, then tell me what she heard instead.");
            else
                Console.WriteLine("The microphone and wake word both work.");
        }
    }

    // Clears the live meter line, prints a message, and lets the meter redraw below it.
    private static void Print(string line)
    {
        lock (ConsoleLock)
        {
            Console.Write("\r" + new string(' ', 79) + "\r");
            Console.WriteLine(line);
        }
    }
}
