using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DesktopSqlStudio.Core;

/// <summary>
/// The kinds of script a query can be (scripts, troubleshooting, cleanup, ...), read from a YAML file.
/// Every script always has exactly one type, and the type is a folder name under each database folder.
/// </summary>
public sealed class ScriptTypes
{
    public const string FallbackDefault = "scripts";
    private static readonly string[] FallbackTypes = { "scripts", "troubleshooting", "cleanup", "analytics" };

    public const string DefaultFileText =
        "# Script types. Each type is a folder under <server>\\<database>\\ and gets a button on the toolbar.\n" +
        "# Add, remove or rename types here, then use File > Reload Config.\n" +
        "default: scripts\n" +
        "types:\n" +
        "  - scripts\n" +
        "  - troubleshooting\n" +
        "  - cleanup\n" +
        "  - analytics\n";

    /// <summary>The type given to new scripts. Always one of <see cref="Names"/>.</summary>
    public string Default { get; }
    public IReadOnlyList<string> Names { get; }
    /// <summary>Set when the YAML could not be read and the built-in types are used instead.</summary>
    public string? Warning { get; }

    public ScriptTypes(string? defaultType, IEnumerable<string>? names, string? warning = null)
    {
        var list = (names ?? Enumerable.Empty<string>())
            .Select(n => n?.Trim() ?? "")
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0) list = FallbackTypes.ToList();

        var chosen = list.FirstOrDefault(n => n.Equals(defaultType?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            chosen = string.IsNullOrWhiteSpace(defaultType) ? list[0] : defaultType.Trim();
            if (!string.IsNullOrWhiteSpace(defaultType)) list.Insert(0, chosen);
        }

        Default = chosen;
        Names = list;
        Warning = warning;
    }

    public static ScriptTypes Fallback(string? warning = null) => new(FallbackDefault, FallbackTypes, warning);

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopSqlStudio", "script-types.yaml");

    /// <summary>Loads the YAML file; creates it with the default types if it does not exist. A damaged file falls back to the built-in types.</summary>
    public static ScriptTypes Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, DefaultFileText);
            }

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var file = deserializer.Deserialize<ScriptTypesFile>(File.ReadAllText(path)) ?? new ScriptTypesFile();
            return new ScriptTypes(file.Default, file.Types);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fallback($"{path} could not be read ({ex.Message}); using the built-in script types.");
        }
    }

    private sealed class ScriptTypesFile
    {
        public string? Default { get; set; }
        public List<string>? Types { get; set; }
    }
}
