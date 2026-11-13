using System.Text.Json;

namespace DesktopSqlStudio.Core;

public sealed class SavedQuery
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Server { get; set; }
    public string? Database { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>
/// Crash-safe storage for query windows. Each window is one .sql file named by its id,
/// so the folder is easy to browse and works well as a git repo. A small session.json
/// remembers which windows were open, their order, titles and connections.
/// Closing a window moves its file to Closed/ instead of deleting it.
/// </summary>
public sealed class QueryStore
{
    private const string SessionFileName = "session.json";
    private const string ClosedFolderName = "Closed";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Folder { get; }

    public QueryStore(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(Folder);
    }

    public static string NewId() => $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    private string QueryPath(string id) => Path.Combine(Folder, id + ".sql");

    public void SaveText(string id, string text) => AtomicFile.WriteAllText(QueryPath(id), text);

    public void SaveSession(IEnumerable<SavedQuery> openQueries)
    {
        var entries = openQueries.Select(q => new SessionEntry { Id = q.Id, Title = q.Title, Server = q.Server, Database = q.Database }).ToList();
        AtomicFile.WriteAllText(Path.Combine(Folder, SessionFileName), JsonSerializer.Serialize(entries, JsonOptions));
    }

    /// <summary>Returns the windows that were open last time, in order. Falls back to loose .sql files if session.json is missing or damaged.</summary>
    public List<SavedQuery> LoadOpen()
    {
        var result = new List<SavedQuery>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sessionPath = Path.Combine(Folder, SessionFileName);
        if (File.Exists(sessionPath))
        {
            try
            {
                var entries = JsonSerializer.Deserialize<List<SessionEntry>>(File.ReadAllText(sessionPath)) ?? new();
                foreach (var e in entries)
                {
                    if (string.IsNullOrEmpty(e.Id) || !File.Exists(QueryPath(e.Id)) || !seen.Add(e.Id)) continue;
                    result.Add(new SavedQuery { Id = e.Id, Title = e.Title, Server = e.Server, Database = e.Database, Text = File.ReadAllText(QueryPath(e.Id)) });
                }
            }
            catch (JsonException) { /* damaged session file: fall through to loose files */ }
        }

        // Any .sql file not in the session (crash before session.json was written) is still recovered.
        foreach (var file in Directory.EnumerateFiles(Folder, "*.sql").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!seen.Add(id)) continue;
            result.Add(new SavedQuery { Id = id, Title = id, Text = File.ReadAllText(file) });
        }
        return result;
    }

    /// <summary>Moves a closed window's file into Closed/ so it can still be recovered.</summary>
    public void MarkClosed(string id)
    {
        var source = QueryPath(id);
        if (!File.Exists(source)) return;
        var closed = Path.Combine(Folder, ClosedFolderName);
        Directory.CreateDirectory(closed);
        File.Move(source, Path.Combine(closed, id + ".sql"), overwrite: true);
    }

    private sealed class SessionEntry
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Server { get; set; }
        public string? Database { get; set; }
    }
}
