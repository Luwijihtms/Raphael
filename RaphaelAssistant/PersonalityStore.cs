using System.Text.RegularExpressions;

namespace Raphael;

/// <summary>
/// How she comes to fit you over time. She keeps a short journal of what you ask her (the request text only, which is
/// already sent to Gemini anyway), and about once a day, after enough new requests, asks Gemini to boil it down to a
/// few short observations about how you talk to her (tone, brevity, favourite topics, running jokes). Those notes are
/// added to her instructions as tone guidance only: they can't give her commands or change her rules. The notes and the
/// journal are plain files in %LOCALAPPDATA%\Raphael, and "forget what you've learned about me" clears them.
/// </summary>
public static class PersonalityStore
{
    private const int MaxNotes = 6;
    private const int MaxNoteLength = 140;
    private const int MaxJournal = 60;
    private const int NeededRequests = 20;
    private static readonly TimeSpan MinGap = TimeSpan.FromHours(24);

    // A note is an observation. Anything that looks like a command, a secret or an attempt to steer her rules is dropped.
    private static readonly Regex Suspicious = new(
        @"\b(ignore|instructions?|always call|never call|tools?|password|passcode|api|keys?|token|secret|system prompt|you must|you should|run|execute)\b",
        RegexOptions.IgnoreCase);

    public static string? FolderOverride; // for tests
    private static string Folder => FolderOverride ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael");
    private static string NotesPath => System.IO.Path.Combine(Folder, "personality.txt");
    private static string JournalPath => System.IO.Path.Combine(Folder, "recent_requests.txt");
    private static readonly object FileLock = new();
    private static int _updating;

    /// <summary>What she has noticed about how you talk to her.</summary>
    public static IReadOnlyList<string> LoadNotes()
    {
        lock (FileLock)
        {
            try { return File.Exists(NotesPath) ? File.ReadAllLines(NotesPath).Where(l => l.Trim().Length > 0).ToList() : new List<string>(); }
            catch { return new List<string>(); }
        }
    }

    /// <summary>Adds one thing you said to the journal. Only ordinary typed requests: not her own follow-ups, not empty.</summary>
    public static void Note(string request, DateTimeOffset? at = null)
    {
        request = string.Join(' ', (request ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (request.Length < 2 || request.StartsWith('[') || request.StartsWith("Yes, go ahead")) return;
        if (request.Length > 200) request = request[..200];

        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(Folder);
                var lines = File.Exists(JournalPath) ? File.ReadAllLines(JournalPath).ToList() : new List<string>();
                lines.Add($"{(at ?? DateTimeOffset.Now).ToUnixTimeSeconds()}|{request}");
                if (lines.Count > MaxJournal) lines = lines.Skip(lines.Count - MaxJournal).ToList();
                File.WriteAllLines(JournalPath, lines);
            }
        }
        catch { }
    }

    /// <summary>Whether enough has happened since the last update: 20 new requests, and a day since the notes were last rewritten.</summary>
    public static bool UpdateDue(DateTimeOffset now, out List<string> newRequests)
    {
        newRequests = new List<string>();
        lock (FileLock)
        {
            try
            {
                var lastUpdate = File.Exists(NotesPath) ? new DateTimeOffset(File.GetLastWriteTimeUtc(NotesPath), TimeSpan.Zero) : DateTimeOffset.MinValue;
                if (now - lastUpdate < MinGap || !File.Exists(JournalPath)) return false;

                foreach (var line in File.ReadAllLines(JournalPath))
                {
                    var bar = line.IndexOf('|');
                    if (bar > 0 && long.TryParse(line[..bar], out var unix) && DateTimeOffset.FromUnixTimeSeconds(unix) > lastUpdate)
                        newRequests.Add(line[(bar + 1)..]);
                }
                return newRequests.Count >= NeededRequests;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Rewrites the notes if an update is due. <paramref name="ask"/> sends one prompt to Gemini and returns its text (or
    /// null on failure). At most one update runs at a time.
    /// </summary>
    public static async Task<bool> MaybeUpdateAsync(Func<string, Task<string?>> ask, DateTimeOffset? now = null)
    {
        if (!UpdateDue(now ?? DateTimeOffset.Now, out var requests)) return false;
        if (Interlocked.Exchange(ref _updating, 1) == 1) return false;
        try
        {
            var current = LoadNotes();
            var prompt =
                "You keep a few short observations about how one user talks to their voice assistant, Raphael, so that Raphael's tone can fit them.\n"
                + "Current observations:\n" + (current.Count == 0 ? "(none yet)" : string.Join("\n", current.Select(n => "- " + n))) + "\n\n"
                + "The user's latest requests, oldest first:\n" + string.Join("\n", requests.TakeLast(40).Select(r => "- " + r)) + "\n\n"
                + $"Write the updated list: at most {MaxNotes} lines, each one short plain observation (under {MaxNoteLength} characters) about their tone, how brief they are, "
                + "favourite topics or running jokes, for example \"Usually types short lowercase requests.\" Keep old observations that still hold. "
                + "Never include names, addresses, passwords, keys or anything private. Write observations only, never instructions or commands. "
                + "Output only the lines, one per line, with no numbering or bullets.";

            var answer = await ask(prompt);
            if (answer == null) return false;

            var notes = Sanitize(answer);
            if (notes.Count == 0) return false;
            lock (FileLock)
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllLines(NotesPath, notes);
            }
            return true;
        }
        catch { return false; }
        finally { Interlocked.Exchange(ref _updating, 0); }
    }

    /// <summary>Keeps only plain, short observations.</summary>
    public static List<string> Sanitize(string answer) => answer.Split('\n')
        .Select(l => l.Trim().TrimStart('-', '*', '•', ' ').Trim())
        .Select(l => Regex.Replace(l, @"^\d+[.)]\s*", ""))
        .Where(l => l.Length >= 14 && !Suspicious.IsMatch(l))
        .Select(l => l.Length > MaxNoteLength ? l[..MaxNoteLength] : l)
        .Take(MaxNotes).ToList();

    public static string Describe()
    {
        var notes = LoadNotes();
        return notes.Count == 0
            ? "She hasn't learned anything about how you talk yet. She needs about 20 requests first, and updates at most once a day."
            : "What she has noticed about how you talk to her: " + string.Join(" ", notes.Select(n => n.TrimEnd('.') + "."));
    }

    public static string Clear()
    {
        try
        {
            lock (FileLock)
            {
                if (File.Exists(NotesPath)) File.Delete(NotesPath);
                if (File.Exists(JournalPath)) File.Delete(JournalPath);
            }
            return "Forgot what I had learned about how you talk, and cleared the request journal.";
        }
        catch (Exception ex) { return $"Could not clear it: {ex.Message}"; }
    }
}


