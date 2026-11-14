using System.Text.RegularExpressions;

namespace DesktopSqlStudio.Core;

/// <summary>One open script's state, independent of any UI framework.</summary>
public sealed class QueryDocument
{
    /// <summary>File name without .sql; always matches <see cref="FilePath"/> once the path is assigned.</summary>
    public string Title { get; set; } = "";
    public string? Server { get; set; }
    public string? Database { get; set; }
    /// <summary>Every script always has a type (scripts, troubleshooting, cleanup, ...).</summary>
    public string Type { get; set; } = ScriptTypes.FallbackDefault;
    /// <summary>Latest editor text. The UI copies its editor text here before saving.</summary>
    public string Text { get; set; } = "";
    public string? FilePath { get; set; }
}

/// <summary>What a node in the tree stands for. Any field may be null when the node is higher up the tree.</summary>
public sealed record CatalogTag(ServerEntry? Server, string? Database, string? Type, string? ScriptPath);

public sealed record TypeFolder(string Type, IReadOnlyList<ScriptFile> Scripts);
public sealed record DatabaseFolder(string Database, IReadOnlyList<TypeFolder> Types);
public sealed record ServerFolder(ServerEntry Server, IReadOnlyList<DatabaseFolder> Databases);
public sealed record GroupFolder(string Name, IReadOnlyList<ServerFolder> Servers);
/// <summary>Everything the tree shows: groups of servers with their databases, types and scripts, plus scripts with no server yet.</summary>
public sealed record Catalog(IReadOnlyList<GroupFolder> Groups, IReadOnlyList<TypeFolder> Unassigned);

/// <summary>
/// All the non-visual logic of the app: open scripts, which server, database and type each one has
/// (which decides its folder), autosaving, config loading and running queries. Front ends only draw it.
/// Methods that touch the disk can throw IOException; the front end decides how to report it.
/// </summary>
public sealed class QueryWorkspace
{
    private readonly List<QueryDocument> _documents = new();

    public AppConfig Config { get; private set; }
    public ScriptTypes Types { get; private set; }
    public string ConfigPath { get; }
    public string TypesPath { get; }
    public QueryStore Store { get; }
    public IReadOnlyList<QueryDocument> Documents => _documents;
    /// <summary>How many scripts were reopened from the last session.</summary>
    public int RestoredCount { get; }
    /// <summary>Set when something went wrong while starting (for example a script file could not be read).</summary>
    public string? StartupWarning { get; }

    public QueryWorkspace(AppConfig config, string configPath, QueryStore store, ScriptTypes types, string typesPath)
    {
        Config = config;
        ConfigPath = configPath;
        Store = store;
        Types = types;
        TypesPath = typesPath;

        try
        {
            Store.MigrateLegacyFiles(Types.Default);
            foreach (var path in Store.LoadSession())
                _documents.Add(DocumentFromFile(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StartupWarning = $"Could not read the scripts folder: {ex.Message}";
        }
        RestoredCount = _documents.Count;
    }

    // ---------- documents ----------

    /// <summary>
    /// Adds a new script. The target is taken from the tree <paramref name="context"/> when a server, database or type
    /// node is selected, otherwise from <paramref name="inheritFrom"/>. Does not write the file: call <see cref="Save"/>.
    /// </summary>
    public QueryDocument NewDocument(QueryDocument? inheritFrom, CatalogTag? context = null)
    {
        var document = new QueryDocument
        {
            Server = inheritFrom?.Server,
            Database = inheritFrom?.Database,
            Type = inheritFrom?.Type ?? Types.Default
        };

        if (context?.Server is { } server)
        {
            document.Server = server.Name;
            document.Database = context.Database ?? server.DefaultDatabase;
            document.Type = context.Type ?? Types.Default;
        }
        else if (context is { Server: null, Type: not null })
        {
            document.Server = null; // a type node under "Unassigned"
            document.Database = null;
            document.Type = context.Type;
        }

        var directory = Store.DirectoryFor(document.Server, document.Database, document.Type);
        var number = NextNumber();
        while (File.Exists(Path.Combine(directory, $"Query {number}.sql"))) number++;
        document.Title = $"Query {number}";

        Store.EnsurePath(document);
        _documents.Add(document);
        return document;
    }

    private int NextNumber() =>
        _documents.Select(d => Regex.Match(d.Title, @"^Query (\d+)$"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(0).Max() + 1;

    /// <summary>Opens the script a tree node points at, or returns the window that already has it open.</summary>
    public QueryDocument? OpenScript(CatalogTag tag)
    {
        if (tag.ScriptPath is null) return null;
        var existing = _documents.FirstOrDefault(d => QueryStore.SamePath(d.FilePath, tag.ScriptPath));
        if (existing is not null) return existing;

        var document = DocumentFromFile(tag.ScriptPath);
        if (tag.Server is not null)
        {
            document.Server = tag.Server.Name;
            document.Database = tag.Database;
        }
        if (tag.Type is not null) document.Type = tag.Type;
        _documents.Add(document);
        return document;
    }

    private QueryDocument DocumentFromFile(string path)
    {
        var (server, database, type) = Locate(path);
        return new QueryDocument
        {
            Title = Path.GetFileNameWithoutExtension(path),
            Server = server,
            Database = database,
            Type = type,
            FilePath = path,
            Text = File.ReadAllText(path)
        };
    }

    /// <summary>Works out server, database and type from a file's folder (root\server\database\type\name.sql).</summary>
    private (string? Server, string? Database, string Type) Locate(string path)
    {
        var parts = Path.GetRelativePath(Store.Folder, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string TypeFor(string folder) =>
            Types.Names.FirstOrDefault(t => QueryStore.Sanitize(t).Equals(folder, StringComparison.OrdinalIgnoreCase)) ?? folder;

        if (parts.Length == 3 && parts[0].Equals(QueryStore.UnassignedFolder, StringComparison.OrdinalIgnoreCase))
            return (null, null, TypeFor(parts[1]));
        if (parts.Length == 4)
        {
            var server = Config.AllServers().FirstOrDefault(s => QueryStore.Sanitize(s.Name).Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            var database = server?.Databases.FirstOrDefault(d => QueryStore.Sanitize(d).Equals(parts[1], StringComparison.OrdinalIgnoreCase)) ?? parts[1];
            return (server?.Name ?? parts[0], database, TypeFor(parts[2]));
        }
        return (null, null, Types.Default);
    }

    /// <summary>Saves the final text and forgets the window. The script stays on disk (and in the tree); an empty one is removed.</summary>
    public void Close(QueryDocument document)
    {
        try
        {
            Store.Save(document);
            Store.DeleteIfEmpty(document);
        }
        finally
        {
            _documents.Remove(document);
        }
    }

    // ---------- autosave ----------

    public void Save(QueryDocument document) => Store.Save(document);

    public void SaveSession() =>
        Store.SaveSession(_documents.Where(d => d.FilePath is not null).Select(d => d.FilePath!));

    /// <summary>Writes every script and the session list to disk right now.</summary>
    public void FlushAll()
    {
        foreach (var document in _documents) Store.Save(document);
        SaveSession();
    }

    // ---------- server, database and type (these decide the folder) ----------

    /// <summary>Changes where a script belongs and moves its file there. The UI must copy the editor text into the document first.</summary>
    public void Retarget(QueryDocument document, string? server, string? database, string type)
    {
        document.Server = server;
        document.Database = database;
        document.Type = type;
        Store.Relocate(document);
        Store.Save(document);
    }

    /// <summary>Points a script at a server. Keeps its database if the server offers it, otherwise uses the server's default.</summary>
    public void SelectServer(QueryDocument document, ServerEntry server)
    {
        var database = server.Databases.Contains(document.Database ?? "", StringComparer.OrdinalIgnoreCase)
            ? document.Database
            : server.DefaultDatabase;
        Retarget(document, server.Name, database, document.Type);
    }

    public void SetDatabase(QueryDocument document, string database) =>
        Retarget(document, document.Server, database, document.Type);

    public void SetType(QueryDocument document, string type) =>
        Retarget(document, document.Server, document.Database, type);

    public ServerEntry? ServerOf(QueryDocument? document) => Config.FindServer(document?.Server);

    // ---------- tree ----------

    public Catalog BuildCatalog()
    {
        var groups = Config.ServerGroups
            .Select(g => new GroupFolder(g.Name, g.Servers
                .Select(s => new ServerFolder(s, s.Databases.Select(d => new DatabaseFolder(d, TypeFolders(s.Name, d))).ToList()))
                .ToList()))
            .ToList();
        var unassigned = TypeFolders(null, null).Where(t => t.Scripts.Count > 0).ToList();
        return new Catalog(groups, unassigned);
    }

    /// <summary>The configured types first (even when empty), then any other type folders found on disk.</summary>
    private List<TypeFolder> TypeFolders(string? server, string? database)
    {
        var result = Types.Names.Select(t => new TypeFolder(t, Store.ScriptsIn(server, database, t))).ToList();
        foreach (var folder in Store.TypeFoldersOnDisk(server, database)
                     .Where(f => !Types.Names.Any(t => QueryStore.Sanitize(t).Equals(f, StringComparison.OrdinalIgnoreCase))))
            result.Add(new TypeFolder(folder, Store.ScriptsIn(server, database, folder)));
        return result;
    }

    // ---------- config ----------

    public void ReloadConfig()
    {
        Config = AppConfig.Load(ConfigPath);
        Types = ScriptTypes.Load(TypesPath);
    }

    /// <summary>A one-line problem summary for the status bar, or null when everything is fine.</summary>
    public string? ConfigWarnings()
    {
        var problems = Config.Validate();
        if (Types.Warning is not null) problems.Add(Types.Warning);
        if (StartupWarning is not null) problems.Add(StartupWarning);
        return problems.Count == 0 ? null : $"Config ({ConfigPath}): " + string.Join(" ", problems);
    }

    // ---------- running ----------

    /// <summary>Runs a script for a window against its server and database. Problems come back as a failed result, not an exception.</summary>
    public async Task<QueryResult> RunAsync(QueryDocument document, string sql, CancellationToken cancellationToken)
    {
        if (!Config.TryResolve(document.Server, document.Database, out var profile, out var error))
        {
            var failed = new QueryResult { Failed = true };
            failed.Messages.Add(error);
            return failed;
        }
        try
        {
            return await QueryExecutor.ExecuteAsync(profile, sql, cancellationToken);
        }
        catch (Exception ex)
        {
            var failed = new QueryResult { Failed = true };
            failed.Messages.Add(ex.Message);
            return failed;
        }
    }
}
