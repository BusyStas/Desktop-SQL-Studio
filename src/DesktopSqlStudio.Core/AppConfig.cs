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

public sealed class ConnectionProfile
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "master";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthType Auth { get; set; } = AuthType.Sql;

    // Sql auth
    public string? UserName { get; set; }
    public string? Password { get; set; }

    // WindowsAs auth
    public string? WindowsDomain { get; set; }
    public string? WindowsUser { get; set; }
    public string? WindowsPassword { get; set; }

    public bool TrustServerCertificate { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 120;

    public override string ToString() => Name;
}

public sealed class AppConfig
{
    /// <summary>Folder where every query window is autosaved. Meant to be its own git repo.</summary>
    public string AutosaveFolder { get; set; } = DefaultAutosaveFolder();

    public List<ConnectionProfile> Connections { get; set; } = new();

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
        return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}
