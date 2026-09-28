namespace Raphael;

/// <summary>
/// Reads a PDF and summarizes it or answers a question about it, by sending the document straight to Gemini (which
/// reads PDFs natively — text, scanned pages and layout alike) rather than trying to extract text locally. Only on
/// request, and the whole file leaves the PC for that one request, so anything genuinely sensitive (financial, medical,
/// legal) is worth being mindful of before asking. Looks the file up in the common folders if only a name is given.
/// </summary>
public static class DocumentReader
{
    private const long MaxBytes = 15 * 1024 * 1024; // Gemini's inline-data limit has more headroom than this; kept modest for speed
    private static readonly string[] SearchFolders =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
    };

    public static string Read(string filename, string? question)
    {
        if (CommandHandler.Llm == null) return "Cannot read documents right now: the model connection isn't available.";
        if (string.IsNullOrWhiteSpace(filename)) return "No file was named.";

        var path = Resolve(filename.Trim());
        if (path == null) return $"Could not find a PDF matching '{filename}' in Downloads, Documents or the Desktop. Give the full path if it's elsewhere.";

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) { return $"Could not open '{Path.GetFileName(path)}': {ex.Message}"; }

        if (bytes.Length > MaxBytes)
            return $"'{Path.GetFileName(path)}' is {bytes.Length / 1_048_576.0:0.#} MB, too large to read this way (limit {MaxBytes / 1_048_576} MB).";

        var prompt = string.IsNullOrWhiteSpace(question)
            ? "Summarize this document in a short spoken-friendly way: what it is, and its main points. A few sentences, plain language, no formatting."
            : $"Looking at this document, answer plainly: {question.Trim()}";

        var answer = CommandHandler.Llm.SideQuestionWithFileAsync(prompt, bytes, "application/pdf", maxOutputTokens: 1024).GetAwaiter().GetResult();
        return answer == null
            ? $"Could not read '{Path.GetFileName(path)}' right now (the model did not answer)."
            : $"From {Path.GetFileName(path)}: {answer}";
    }

    /// <summary>A path as given, or a filename (with or without .pdf) found under Downloads/Documents/Desktop — an exact
    /// name match wins over a partial one.</summary>
    private static string? Resolve(string filename)
    {
        if (File.Exists(filename)) return filename;
        var withExt = filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? filename : filename + ".pdf";
        if (File.Exists(withExt)) return withExt;

        string? partial = null;
        foreach (var folder in SearchFolders)
        {
            if (!Directory.Exists(folder)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.pdf"); } catch { continue; }

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.Equals(name, Path.GetFileNameWithoutExtension(filename), StringComparison.OrdinalIgnoreCase)) return file;
                if (partial == null && name.Contains(filename, StringComparison.OrdinalIgnoreCase)) partial = file;
            }
        }
        return partial;
    }
}
