using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace ClawTui.Agent;

/// <summary>
/// Coding tools exposed to the agent. Read-only tools are auto-approved;
/// mutating tools go through the TUI approval prompt (see PermissionPolicy).
/// All paths are confined to <see cref="WorkingDirectory"/>.
/// </summary>
public sealed class CodingTools
{
    public string WorkingDirectory { get; }

    /// <summary>Commands that are always refused, even if the user approves (defense in depth).</summary>
    private static readonly string[] HardDenyPatterns =
    [
        @"\brm\s+(-[a-z]*r[a-z]*\s+)?/\s",          // rm -rf /
        @"\brm\s+(-[a-z]*r[a-z]*\s+)~/",            // rm -rf ~/
        @":\(\)\s*\{\s*:\|\:&\s*\}\s*;",            // fork bomb
        @"\bmkfs\b", @"\bdd\b.*\bof=/dev/",
        @"\bshutdown\b", @"\breboot\b", @"\bhalt\b",
        @"\bchmod\s+-R\s+777\s+/", @"\bchown\s+-R\b.*\s+/$",
    ];

    private const int MaxOutputChars = 30_000;
    private const int MaxFileChars = 60_000;

    public CodingTools(string workingDirectory)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory);
    }

    // ---------- path safety ----------

    private string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(WorkingDirectory, path));
        if (!full.StartsWith(WorkingDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(full, WorkingDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Path escapes working directory: {path}");
        }
        return full;
    }

    // ---------- read-only tools (auto-approved) ----------

    [Description("Reads a file's contents with line numbers. Use offset/limit to page through large files.")]
    public string ReadFile(
        [Description("Path relative to the working directory.")] string path,
        [Description("First line to read (1-based).")] int offset = 1,
        [Description("Max lines to read.")] int limit = 500)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
        {
            return $"Error: file not found: {path}";
        }
        var lines = File.ReadAllLines(full);
        var sb = new StringBuilder();
        int start = Math.Max(0, offset - 1);
        int end = Math.Min(lines.Length, start + Math.Max(1, limit));
        for (int i = start; i < end; i++)
        {
            sb.AppendLine($"{i + 1,6}: {lines[i]}");
        }
        if (end < lines.Length)
        {
            sb.AppendLine($"… ({lines.Length - end} more lines, use offset={end + 1})");
        }
        return Truncate(sb.ToString(), MaxFileChars);
    }

    [Description("Finds files by glob pattern, e.g. '**/*.cs', 'src/**/*.py'. Searches under the working directory.")]
    public string GlobFiles(
        [Description("Glob pattern relative to the working directory.")] string pattern)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern.TrimStart('/'));
        var matches = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(WorkingDirectory)));
        var files = matches.Files.Select(f => f.Path).OrderBy(p => p).Take(200).ToList();
        if (files.Count == 0)
        {
            return $"No files matched: {pattern}";
        }
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            sb.AppendLine(f);
        }
        if (matches.Files.Count() > 200)
        {
            sb.AppendLine($"… ({matches.Files.Count() - 200} more)");
        }
        return sb.ToString();
    }

    [Description("Searches file contents with a regular expression. Returns file:line matches.")]
    public string Grep(
        [Description("Regular expression to search for.")] string pattern,
        [Description("File or directory to search (default: working directory).")] string? path = null,
        [Description("Only search files matching this glob, e.g. '*.cs'.")] string? includeGlob = null)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled);
        }
        catch (Exception ex)
        {
            return $"Error: invalid regex: {ex.Message}";
        }

        var root = string.IsNullOrEmpty(path) ? WorkingDirectory : Resolve(path);
        IEnumerable<string> files = File.Exists(root)
            ? [root]
            : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);

        Matcher? matcher = null;
        if (!string.IsNullOrEmpty(includeGlob))
        {
            matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(includeGlob);
        }

        var sb = new StringBuilder();
        int hits = 0;
        foreach (var file in files)
        {
            if (hits >= 100)
            {
                sb.AppendLine("… (truncated at 100 matches)");
                break;
            }
            if (matcher is not null)
            {
                var rel = Path.GetRelativePath(WorkingDirectory, file);
                if (!matcher.Match(rel).HasMatches)
                {
                    continue;
                }
            }
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch
            {
                continue; // binary / unreadable
            }
            for (int i = 0; i < lines.Length && hits < 100; i++)
            {
                if (regex.IsMatch(lines[i]))
                {
                    var rel = Path.GetRelativePath(WorkingDirectory, file);
                    sb.AppendLine($"{rel}:{i + 1}: {lines[i].Trim()}");
                    hits++;
                }
            }
        }
        return hits == 0 ? $"No matches for: {pattern}" : sb.ToString();
    }

    // ---------- mutating tools (approval required) ----------

    [Description("Writes (creates or overwrites) a file. Requires user approval.")]
    public string WriteFile(
        [Description("Path relative to the working directory.")] string path,
        [Description("Full content to write.")] string content)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return $"Wrote {content.Length} chars to {path}.";
    }

    [Description("Replaces the first exact occurrence of oldText with newText in a file. Requires user approval. Fails if oldText is not unique.")]
    public string EditFile(
        [Description("Path relative to the working directory.")] string path,
        [Description("Exact text to find (must occur exactly once).")] string oldText,
        [Description("Replacement text.")] string newText)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
        {
            return $"Error: file not found: {path}";
        }
        var content = File.ReadAllText(full);
        int count = 0, idx = 0;
        while ((idx = content.IndexOf(oldText, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += oldText.Length;
        }
        if (count == 0)
        {
            return $"Error: oldText not found in {path}.";
        }
        if (count > 1)
        {
            return $"Error: oldText occurs {count} times in {path}; it must be unique. Include more context.";
        }
        File.WriteAllText(full, content.Replace(oldText, newText, StringComparison.Ordinal));
        return $"Edited {path}: replaced {oldText.Length} chars with {newText.Length} chars.";
    }

    [Description("Runs a shell command in the working directory. Requires user approval. Output is truncated.")]
    public string RunBash(
        [Description("Shell command to run (bash -c).")] string command,
        [Description("Timeout in seconds.")] int timeoutSeconds = 30)
    {
        foreach (var deny in HardDenyPatterns)
        {
            if (Regex.IsMatch(command, deny, RegexOptions.IgnoreCase))
            {
                return $"Refused: command matches a hard-deny safety pattern and will not run: {command}";
            }
        }

        var psi = new ProcessStartInfo("/bin/bash", ["-c", command])
        {
            WorkingDirectory = WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Confine common destructive env behavior: no sudo without password prompts hanging the agent.
        psi.Environment["SUDO_ASKPASS"] = "/bin/false";

        using var proc = Process.Start(psi)!;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300))))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // already exited
            }
            return $"Error: command timed out after {timeoutSeconds}s and was killed: {command}";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"exit code: {proc.ExitCode}");
        if (stdout.Length > 0)
        {
            sb.AppendLine("--- stdout ---");
            sb.Append(Truncate(stdout.ToString(), MaxOutputChars));
        }
        if (stderr.Length > 0)
        {
            sb.AppendLine("--- stderr ---");
            sb.Append(Truncate(stderr.ToString(), MaxOutputChars));
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"\n… (truncated, {s.Length - max} more chars)";
}
