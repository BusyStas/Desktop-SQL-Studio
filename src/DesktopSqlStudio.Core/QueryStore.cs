using System.Text.Json;

namespace DesktopSqlStudio.Core;

/// <summary>A script on disk shown in the tree.</summary>
public sealed record ScriptFile(string Name, string Path);

/// <summary>
/// Crash-safe storage for scripts, laid out as <c>root\server\database\type\name.sql</c>.
/// Scripts that have no server yet live in <c>root\_unassigned\type\name.sql</c>.
/// A small session.json at the root remembers which scripts were open, in order.
/// Nothing in here ever deletes a script that has content.
/// </summary>
public sealed class QueryStore
{
    public const string UnassignedFolder = "_unassigned";
    private const string NoDatabaseFolder = "_default";
    private const string SessionFileName = "session.json";

    public string Folder { get; }

    public QueryStore(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(Folder);
    }

    // ---------- paths ----------

    /// <summary>Makes a name safe to use as a folder or file name.</summary>
    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    public string DirectoryFor(string? server, string? database, string type) =>
        string.IsNullOrWhiteSpace(server)
            ? Path.Combine(Folder, UnassignedFolder, Sanitize(type))
            : Path.Combine(Folder, Sanitize(server), string.IsNullOrWhiteSpace(database) ? NoDatabaseFolder : Sanitize(database), Sanitize(type));

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string UniqueName(string directory, string title)
    {
        var name = Sanitize(title);
        if (!File.Exists(Path.Combine(directory, name + ".sql"))) return name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} ({n})";
            if (!File.Exists(Path.Combine(directory, candidate + ".sql"))) return candidate;
        }
    }

    /// <summary>Gives a document its file path (no disk writes). The title may get a " (2)" suffix to stay unique.</summary>
    public void EnsurePath(QueryDocument document)
    {
        if (document.FilePath is not null) return;
        var directory = DirectoryFor(document.Server, document.Database, document.Type);
        var name = UniqueName(directory, document.Title);
        document.FilePath = Path.Combine(directory, name + ".sql");
        document.Title = name;
    }

    // ---------- saving ----------

    public void Save(QueryDocument document)
    {
        EnsurePath(document);
        Directory.CreateDirectory(Path.GetDirectoryName(document.FilePath!)!);
        AtomicFile.WriteAllText(document.FilePath!, document.Text);
    }

    /// <summary>Moves the document's file to the folder for its current server, database and type.</summary>
    public void Relocate(QueryDocument document)
    {
        var targetDirectory = DirectoryFor(document.Server, document.Database, document.Type);
        if (document.FilePath is not null && SamePath(Path.GetDirectoryName(document.FilePath), targetDirectory)) return;

        var oldPath = document.FilePath;
        var name = UniqueName(targetDirectory, document.Title);
        var newPath = Path.Combine(targetDirectory, name + ".sql");

        if (oldPath is not null && File.Exists(oldPath))
        {
            Directory.CreateDirectory(targetDirectory);
            File.Move(oldPath, newPath);
        }
        document.FilePath = newPath;
        document.Title = name;
    }

    /// <summary>Removes the file of an empty script (nothing to lose). Scripts with content are never deleted.</summary>
    public void DeleteIfEmpty(QueryDocument document)
    {
        if (document.FilePath is null || !string.IsNullOrWhiteSpace(document.Text)) return;
        if (File.Exists(document.FilePath) && string.IsNullOrWhiteSpace(File.ReadAllText(document.FilePath)))
            File.Delete(document.FilePath);
    }

    // ---------- listing (for the tree) ----------

    /// <summary>Names of the type folders that exist on disk for a server and database (or for unassigned scripts).</summary>
    public IReadOnlyList<string> TypeFoldersOnDisk(string? server, string? database)
    {
        var parent = string.IsNullOrWhiteSpace(server)
            ? Path.Combine(Folder, UnassignedFolder)
            : Path.Combine(Folder, Sanitize(server), string.IsNullOrWhiteSpace(database) ? NoDatabaseFolder : Sanitize(database));
        return Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent).Select(d => Path.GetFileName(d)!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
    }

    public IReadOnlyList<ScriptFile> ScriptsIn(string? server, string? database, string type)
    {
        var directory = DirectoryFor(server, database, type);
        if (!Directory.Exists(directory)) return new List<ScriptFile>();
        return Directory.EnumerateFiles(directory)
            .Where(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ScriptFile(Path.GetFileNameWithoutExtension(f), f))
            .ToList();
    }

    // ---------- session ----------

    public void SaveSession(IEnumerable<string> openFilePaths)
    {
        var relative = openFilePaths.Select(p => Path.GetRelativePath(Folder, p)).ToList();
        AtomicFile.WriteAllText(Path.Combine(Folder, SessionFileName), JsonSerializer.Serialize(relative, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Files that were open last time and still exist, in order.</summary>
    public List<string> LoadSession()
    {
        var path = Path.Combine(Folder, SessionFileName);
        if (!File.Exists(path)) return new List<string>();
        try
        {
            var relative = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new();
            return relative.Select(r => Path.GetFullPath(Path.Combine(Folder, r))).Where(File.Exists).ToList();
        }
        catch (JsonException)
        {
            return new List<string>(); // old or damaged session file: scripts are still in the tree
        }
    }

    /// <summary>Moves .sql files left in the root by the first version into _unassigned\&lt;type&gt;\ so they show up in the tree.</summary>
    public void MigrateLegacyFiles(string defaultType)
    {
        var loose = Directory.EnumerateFiles(Folder).Where(f => f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).ToList();
        if (loose.Count == 0) return;
        var target = DirectoryFor(null, null, defaultType);
        Directory.CreateDirectory(target);
        foreach (var file in loose)
        {
            var name = UniqueName(target, Path.GetFileNameWithoutExtension(file));
            File.Move(file, Path.Combine(target, name + ".sql"));
        }
    }
}
