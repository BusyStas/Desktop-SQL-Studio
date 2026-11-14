using System.Text.RegularExpressions;

namespace DesktopSqlStudio.Core;

/// <summary>One query window's state, independent of any UI framework.</summary>
public sealed class QueryDocument
{
    public string Id { get; init; } = "";
    public string Title { get; set; } = "";
    public string? Server { get; set; }
    public string? Database { get; set; }
    /// <summary>Latest editor text. The UI copies its editor text here before saving.</summary>
    public string Text { get; set; } = "";
}

/// <summary>
/// All the non-visual logic of the app: which query windows exist, which server and database each
/// one uses, autosaving, config loading and running queries. Front ends (WPF, WinForms, ...) only
/// draw it. Methods that touch the disk can throw IOException; the front end decides how to report it.
/// </summary>
public sealed class QueryWorkspace
{
    private readonly List<QueryDocument> _documents = new();

    public AppConfig Config { get; private set; }
    public string ConfigPath { get; }
    public QueryStore Store { get; }
    public IReadOnlyList<QueryDocument> Documents => _documents;
    /// <summary>How many windows were restored from the autosave folder at startup.</summary>
    public int RestoredCount { get; }

    public QueryWorkspace(AppConfig config, string configPath, QueryStore store)
    {
        Config = config;
        ConfigPath = configPath;
        Store = store;

        foreach (var saved in store.LoadOpen())
        {
            _documents.Add(new QueryDocument
            {
                Id = saved.Id,
                Title = saved.Title,
                Server = saved.Server,
                Database = saved.Database,
                Text = saved.Text
            });
        }
        RestoredCount = _documents.Count;
    }

    // ---------- documents ----------

    /// <summary>Adds a new window. It inherits the server and database of <paramref name="inheritFrom"/>. Does not touch the disk: call <see cref="Save"/> and <see cref="SaveSession"/>.</summary>
    public QueryDocument NewDocument(QueryDocument? inheritFrom)
    {
        var document = new QueryDocument
        {
            Id = QueryStore.NewId(),
            Title = $"Query {NextNumber()}",
            Server = inheritFrom?.Server,
            Database = inheritFrom?.Database
        };
        _documents.Add(document);
        return document;
    }

    private int NextNumber() =>
        _documents.Select(d => Regex.Match(d.Title, @"^Query (\d+)$"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(0).Max() + 1;

    /// <summary>Saves the final text, moves the file to Closed/ (never deletes) and forgets the window.</summary>
    public void Close(QueryDocument document)
    {
        try
        {
            Store.SaveText(document.Id, document.Text);
            Store.MarkClosed(document.Id);
        }
        finally
        {
            _documents.Remove(document);
        }
    }

    // ---------- autosave ----------

    public void Save(QueryDocument document) => Store.SaveText(document.Id, document.Text);

    public void SaveSession() => Store.SaveSession(_documents.Select(ToSaved));

    /// <summary>Writes every window and the session list to disk right now.</summary>
    public void FlushAll()
    {
        foreach (var document in _documents) Store.SaveText(document.Id, document.Text);
        SaveSession();
    }

    private static SavedQuery ToSaved(QueryDocument d) =>
        new() { Id = d.Id, Title = d.Title, Server = d.Server, Database = d.Database, Text = d.Text };

    // ---------- servers and databases ----------

    /// <summary>Points a window at a server. Keeps its database if the server offers it, otherwise uses the server's default.</summary>
    public void SelectServer(QueryDocument document, ServerEntry server)
    {
        document.Server = server.Name;
        if (!server.Databases.Contains(document.Database ?? "", StringComparer.OrdinalIgnoreCase))
            document.Database = server.DefaultDatabase;
    }

    public ServerEntry? ServerOf(QueryDocument? document) => Config.FindServer(document?.Server);

    // ---------- config ----------

    public void ReloadConfig() => Config = AppConfig.Load(ConfigPath);

    /// <summary>A one-line problem summary for the status bar, or null when the config is fine.</summary>
    public string? ConfigWarnings()
    {
        var problems = Config.Validate();
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
