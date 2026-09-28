using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Raphael;

/// <summary>
/// Lets Raphael write a small program, show it to you in Notepad, and — only once you separately confirm — compile
/// and run it (the same "look, then confirm, then act" pattern as view_screen -> close_app; the system prompt is what
/// enforces the pause between write and run, not this class). Supports C#, Java, JavaScript, C and Python — whichever
/// of those toolchains are actually installed; a missing one fails with a plain "not found" rather than a crash.
/// </summary>
public static class CodeSandbox
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Raphael Code");
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Saves the code and opens it for you to read (VS Code if it's installed, for syntax highlighting; Notepad
    /// otherwise). Never compiles or runs it.</summary>
    public static string Write(string language, string filename, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "No code was given.";
        var ext = ExtensionFor(language);
        if (ext == null) return $"Unsupported language '{language}'. Supported: csharp, java, javascript, c, python.";

        var name = SafeName(filename, ext);
        if (ext == ".java")
        {
            // javac requires the file name to match the public class name.
            var className = ExtractJavaClassName(code);
            if (className != null) name = className + ".java";
        }

        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name);
        File.WriteAllText(path, code, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var openedWith = OpenForReading(path);

        var lines = code.Split('\n').Length;
        return $"Wrote {name} ({lines} line{(lines == 1 ? "" : "s")}) to Documents\\Raphael Code and opened it in {openedWith}. Not run yet. Path: {path}";
    }

    /// <summary>Opens the file in VS Code (with syntax highlighting) if it's installed, else falls back to Notepad. Returns
    /// which one it used.</summary>
    private static string OpenForReading(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("code") { UseShellExecute = true };
            psi.ArgumentList.Add("--reuse-window"); // opens in the existing VS Code window instead of spawning a new one
            psi.ArgumentList.Add(path);
            if (Process.Start(psi) != null) return "VS Code";
        }
        catch { /* VS Code's "code" command isn't available; fall back below */ }

        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); return "Notepad"; }
        catch { return "your default text editor"; } // saved either way; worst case you open it yourself
    }

    /// <summary>Compiles (if needed) and runs a file written by <see cref="Write"/>. <paramref name="stdin"/>, if given, is fed
    /// to the program's input (one line each) before its input is closed — needed for anything that reads console input
    /// (scanf, Console.ReadLine, input()), which otherwise gets nothing to read and fails immediately.</summary>
    public static string Run(string path, string? stdin = null)
    {
        path = (path ?? "").Trim().Trim('"');
        if (!File.Exists(path)) return $"Could not find the file '{path}'. Write it first with write_code.";

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => RunCSharp(path, stdin),
            ".java" => RunJava(path, stdin),
            ".js" => RunNode(path, stdin),
            ".c" => RunC(path, stdin),
            ".py" => RunPython(path, stdin),
            _ => $"Don't know how to run '{Path.GetFileName(path)}'."
        };
    }

    // ---- per-language ------------------------------------------------------------------------------------

    private static string RunCSharp(string sourceFile, string? stdin)
    {
        var work = Path.Combine(Path.GetTempPath(), "RaphaelCodeRun", "csharp");
        Directory.CreateDirectory(work);

        // Scaffold the throwaway console project only once; reusing it means later runs skip the NuGet restore.
        if (!Directory.EnumerateFiles(work, "*.csproj").Any())
        {
            var (newExit, _, newErr) = RunProcess("dotnet", work, TimeSpan.FromSeconds(60), null, "new", "console", "-o", work);
            if (newExit != 0) return $"Could not prepare the C# project: {Shorten(newErr)}";
        }

        File.Copy(sourceFile, Path.Combine(work, "Program.cs"), overwrite: true);
        var (exit, output, error) = RunProcess("dotnet", work, RunTimeout, stdin, "run", "--project", work);
        return Format(exit, output, error);
    }

    private static string RunJava(string sourceFile, string? stdin)
    {
        var dir = Path.GetDirectoryName(sourceFile)!;
        var file = Path.GetFileName(sourceFile);
        var className = Path.GetFileNameWithoutExtension(sourceFile);

        var (compileExit, _, compileErr) = RunProcess("javac", dir, TimeSpan.FromSeconds(20), null, file);
        if (compileExit != 0) return $"Compilation failed: {Shorten(compileErr)}";

        var (exit, output, error) = RunProcess("java", dir, RunTimeout, stdin, "-cp", dir, className);
        return Format(exit, output, error);
    }

    private static string RunNode(string sourceFile, string? stdin)
    {
        var (exit, output, error) = RunProcess("node", Path.GetDirectoryName(sourceFile)!, RunTimeout, stdin, sourceFile);
        return Format(exit, output, error);
    }

    private static string RunC(string sourceFile, string? stdin)
    {
        var dir = Path.GetDirectoryName(sourceFile)!;
        var exeName = Path.GetFileNameWithoutExtension(sourceFile) + ".exe";

        var (compileExit, _, compileErr) = RunProcess("gcc", dir, TimeSpan.FromSeconds(20), null, Path.GetFileName(sourceFile), "-o", exeName);
        if (compileExit != 0) return $"Compilation failed: {Shorten(compileErr)}";

        var (exit, output, error) = RunProcess(Path.Combine(dir, exeName), dir, RunTimeout, stdin);
        return Format(exit, output, error);
    }

    private static string RunPython(string sourceFile, string? stdin)
    {
        var (exit, output, error) = RunProcess("python", Path.GetDirectoryName(sourceFile)!, RunTimeout, stdin, sourceFile);
        return Format(exit, output, error);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static string Format(int exit, string output, string error)
    {
        output = output.Trim();
        error = error.Trim();
        if (exit != 0) return $"Ran with an error (exit {exit}): {Shorten(error.Length > 0 ? error : output)}";
        return output.Length == 0 ? "Ran successfully with no output." : $"Output: {Shorten(output)}";
    }

    private static string Shorten(string text) => text.Length > 500 ? text[..500] + "…" : text;

    private static string? ExtensionFor(string language) => language.Trim().ToLowerInvariant() switch
    {
        "csharp" or "c#" or "cs" => ".cs",
        "java" => ".java",
        "javascript" or "js" or "node" or "nodejs" => ".js",
        "c" => ".c",
        "python" or "py" or "python3" => ".py",
        _ => null
    };

    private static string SafeName(string filename, string ext)
    {
        var name = Path.GetFileNameWithoutExtension((filename ?? "").Trim());
        name = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (name.Length == 0) name = $"code-{DateTime.Now:yyyyMMdd-HHmmss}";
        return name + ext;
    }

    private static string? ExtractJavaClassName(string code)
    {
        var m = Regex.Match(code, @"public\s+(?:final\s+|abstract\s+)?class\s+(\w+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <param name="stdinText">Written to the process's input, then closed. Null/empty just closes it right away (the
    /// old behaviour) — right for anything that takes no input, and the only safe default for something unknown, since
    /// leaving input open with nothing behind it is exactly what lets a waiting program hang until the timeout.</param>
    private static (int Exit, string Output, string Error) RunProcess(string exe, string workingDir, TimeSpan timeout, string? stdinText, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? process;
        try { process = Process.Start(psi); }
        catch (Exception ex) { return (-1, "", $"Could not start {exe}: {ex.Message}"); }
        if (process == null) return (-1, "", $"Could not start {exe}.");

        if (!string.IsNullOrEmpty(stdinText))
        {
            process.StandardInput.Write(stdinText.Replace("\r\n", "\n"));
            if (!stdinText.EndsWith('\n')) process.StandardInput.Write('\n'); // most scanf/Scanner/input() reads need a trailing newline
        }
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "", "did not finish in time (it may be stuck in a loop or waiting for input) and was stopped");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
