using System.Text.Json;

namespace ClawTui.Session;

/// <summary>
/// Persists conversation transcripts as JSONL under ~/.clawtui/sessions/
/// so a session can be resumed with --resume.
/// </summary>
public sealed class TranscriptStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".clawtui", "sessions");

    public string SessionId { get; }
    private readonly string _path;
    private readonly object _lock = new();

    public TranscriptStore(string? sessionId = null)
    {
        SessionId = sessionId ?? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(Dir);
        _path = Path.Combine(Dir, SessionId + ".jsonl");
    }

    public void Append(string role, string text)
    {
        var line = JsonSerializer.Serialize(new
        {
            t = DateTime.UtcNow,
            role,
            text,
        });
        lock (_lock)
        {
            File.AppendAllText(_path, line + "\n");
        }
    }

    public IEnumerable<(string Role, string Text)> Load()
    {
        if (!File.Exists(_path))
        {
            yield break;
        }
        foreach (var line in File.ReadLines(_path))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            yield return (
                root.GetProperty("role").GetString() ?? "?",
                root.GetProperty("text").GetString() ?? "");
        }
    }

    public static string? MostRecent()
    {
        if (!Directory.Exists(Dir))
        {
            return null;
        }
        return Directory.EnumerateFiles(Dir, "*.jsonl")
            .OrderByDescending(f => f)
            .Select(Path.GetFileNameWithoutExtension)
            .FirstOrDefault();
    }
}
