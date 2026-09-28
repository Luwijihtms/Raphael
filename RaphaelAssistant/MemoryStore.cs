namespace Raphael;

/// <summary>
/// Raphael's persistent knowledge, stored under %LOCALAPPDATA%\Raphael (outside the project):
///   memory.txt        — facts she saved with the remember tool, one per line
///   instructions.txt  — your own standing instructions, edited by hand (no rebuild needed)
/// </summary>
public static class MemoryStore
{
    private const int MaxFacts = 200;
    private const int MaxFactLength = 300;

    private static readonly object FileLock = new();

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Raphael");
    private static string MemoryPath => Path.Combine(Folder, "memory.txt");
    private static string InstructionsPath => Path.Combine(Folder, "instructions.txt");

    private const string InstructionsTemplate = """
        # Extra instructions for Raphael. Lines starting with # are ignored.
        # Edit this file and restart Raphael; no rebuild needed. Remove the # to switch a line on.
        #
        # Address me as "Master" (マスター).
        # Begin factual answers with 「告。」 and explanations with 「解。」.
        # My name is ...
        # I mostly use my PC for ...
        """;

    public static List<string> LoadFacts()
    {
        lock (FileLock)
        {
            try
            {
                return File.Exists(MemoryPath)
                    ? File.ReadAllLines(MemoryPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
                    : new List<string>();
            }
            catch { return new List<string>(); }
        }
    }

    public static string Add(string fact)
    {
        // One fact per line: flatten whitespace and cap the length.
        fact = string.Join(' ', (fact ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (fact.Length == 0) return "Nothing to remember.";
        if (fact.Length > MaxFactLength) fact = fact[..MaxFactLength];

        lock (FileLock)
        {
            var facts = LoadFacts();
            if (facts.Any(f => f.Equals(fact, StringComparison.OrdinalIgnoreCase)))
                return "Already remembered.";
            if (facts.Count >= MaxFacts)
                return $"Memory is full ({MaxFacts} facts). Ask me to forget something first.";

            facts.Add(fact);
            Save(facts);
            return $"Remembered: {fact}";
        }
    }

    /// <summary>Removes every saved fact containing the keyword and reports what was removed.</summary>
    public static string Remove(string keyword)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length < 3) return "Give a keyword of at least three characters so that only the intended fact is removed.";

        lock (FileLock)
        {
            var facts = LoadFacts();
            var removed = facts.Where(f => f.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            if (removed.Count == 0) return $"No saved fact mentions '{keyword}'.";

            Save(facts.Except(removed).ToList());
            return $"Forgot {removed.Count} fact(s): {string.Join(" | ", removed)}";
        }
    }

    /// <summary>The user's hand-written instructions (comments stripped). Creates the template on first use.</summary>
    public static string LoadInstructions()
    {
        try
        {
            if (!File.Exists(InstructionsPath))
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(InstructionsPath, InstructionsTemplate);
            }

            var lines = File.ReadAllLines(InstructionsPath)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'));
            return string.Join("\n", lines);
        }
        catch { return ""; }
    }

    private static void Save(List<string> facts)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllLines(MemoryPath, facts);
    }
}
