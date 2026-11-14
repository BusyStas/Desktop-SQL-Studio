using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopSqlStudio.Core;

public enum AuthType
{
    /// <summary>SQL Server login (user name + password from config).</summary>
    Sql,
    /// <summary>Current Windows user (integrated security).</summary>
    Windows,
    /// <summary>A different Windows user, credentials from config.</summary>
    WindowsAs
}

/// <summary>A named login. Servers refer to it by name so a password is stored only once.</summary>
public sealed class CredentialEntry
{
    public string Name { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthType Auth { get; set; } = AuthType.Sql;

    // Sql auth
    public string? UserName { get; set; }
    public string? Password { get; set; }

    // WindowsAs auth
    public string? WindowsDomain { get; set; }
    public string? WindowsUser { get; set; }
    public string? WindowsPassword { get; set; }
}

public sealed class ServerEntry
{
    /// <summary>Unique across all groups; this is what a query window remembers.</summary>
    public string Name { get; set; } = "";
    /// <summary>Host name, host\instance, or host,port.</summary>
    public string Address { get; set; } = "";
    /// <summary>Name of an entry in <see cref="AppConfig.Credentials"/>.</summary>
    public string Credential { get; set; } = "";
    public string DefaultDatabase { get; set; } = "master";
    /// <summary>Databases offered in the database dropdown. The default database is always included.</summary>
    public List<string> Databases { get; set; } = new();
    public bool TrustServerCertificate { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 120;

    public override string ToString() => Name;
}

public sealed class ServerGroup
{
    public string Name { get; set; } = "";
    public List<ServerEntry> Servers { get; set; } = new();
}

/// <summary>Everything needed to open one connection: a server, a database and a login.</summary>
public sealed class ConnectionProfile
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "master";
    public AuthType Auth { get; set; } = AuthType.Sql;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? WindowsDomain { get; set; }
    public string? WindowsUser { get; set; }
    public string? WindowsPassword { get; set; }
    public bool TrustServerCertificate { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 120;
}

public sealed class AppConfig
{
    /// <summary>Folder where every query window is autosaved. Meant to be its own git repo.</summary>
    public string AutosaveFolder { get; set; } = DefaultAutosaveFolder();

    /// <summary>True when the file still uses the old "Connections" list, which is no longer read.</summary>
    [JsonIgnore]
    public bool HasLegacyConnections { get; private set; }

    public List<CredentialEntry> Credentials { get; set; } = new();
    public List<ServerGroup> ServerGroups { get; set; } = new();

    public static string DefaultAutosaveFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DesktopSqlStudio", "Queries");

    public static string DefaultConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopSqlStudio", "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Loads the config; creates an empty one if the file does not exist.</summary>
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var fresh = new AppConfig();
            fresh.Save(path);
            return fresh;
        }
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
        config.HasLegacyConnections = HasProperty(json, "Connections");
        config.Normalize();
        return config;
    }

    private static bool HasProperty(string json, string name)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement.ValueKind == JsonValueKind.Object &&
               doc.RootElement.EnumerateObject().Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>Makes sure each server's default database is in its database list and the list has no duplicates.</summary>
    public void Normalize()
    {
        foreach (var server in AllServers())
        {
            if (string.IsNullOrWhiteSpace(server.DefaultDatabase)) server.DefaultDatabase = "master";
            var list = new List<string> { server.DefaultDatabase };
            list.AddRange(server.Databases.Where(d => !string.IsNullOrWhiteSpace(d)));
            server.Databases = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public IEnumerable<ServerEntry> AllServers() => ServerGroups.SelectMany(g => g.Servers);

    public ServerEntry? FindServer(string? name) =>
        string.IsNullOrEmpty(name) ? null : AllServers().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Problems worth showing to the user: duplicate server names, unknown credentials.</summary>
    public List<string> Validate()
    {
        var problems = new List<string>();
        if (HasLegacyConnections)
            problems.Add("config.json uses the old \"Connections\" format, which is ignored. Use \"Credentials\" and \"ServerGroups\" (see config.example.json).");
        if (ServerGroups.Count == 0 || !AllServers().Any())
            problems.Add("No servers defined in config.json.");
        foreach (var dup in AllServers().GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Server name '{dup.Key}' is used more than once.");
        foreach (var dup in Credentials.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Credential name '{dup.Key}' is used more than once.");
        foreach (var s in AllServers().Where(s => FindCredential(s.Credential) is null))
            problems.Add($"Server '{s.Name}' uses unknown credential '{s.Credential}'.");
        return problems;
    }

    public CredentialEntry? FindCredential(string? name) =>
        Credentials.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Combines a server, a database and the server's credential into a connection profile.</summary>
    public bool TryResolve(string? serverName, string? database, out ConnectionProfile profile, out string error)
    {
        profile = new ConnectionProfile();
        error = "";

        var server = FindServer(serverName);
        if (server is null) { error = "Pick a server in the tree on the left."; return false; }

        var credential = FindCredential(server.Credential);
        if (credential is null) { error = $"Server '{server.Name}' uses unknown credential '{server.Credential}'."; return false; }

        profile = new ConnectionProfile
        {
            Name = server.Name,
            Server = server.Address,
            Database = string.IsNullOrWhiteSpace(database) ? server.DefaultDatabase : database,
            Auth = credential.Auth,
            UserName = credential.UserName,
            Password = credential.Password,
            WindowsDomain = credential.WindowsDomain,
            WindowsUser = credential.WindowsUser,
            WindowsPassword = credential.WindowsPassword,
            TrustServerCertificate = server.TrustServerCertificate,
            CommandTimeoutSeconds = server.CommandTimeoutSeconds
        };
        return true;
    }
}
