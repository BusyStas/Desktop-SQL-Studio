using DesktopSqlStudio.Core;
using Xunit;

namespace DesktopSqlStudio.Tests;

public class ScriptTypesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dss-types-" + Guid.NewGuid().ToString("N"));
    private string YamlPath => Path.Combine(_folder, "script-types.yaml");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void MissingFileIsCreatedWithTheDefaultTypes()
    {
        var types = ScriptTypes.Load(YamlPath);

        Assert.True(File.Exists(YamlPath));
        Assert.Equal(new[] { "scripts", "troubleshooting", "cleanup", "analytics" }, types.Names);
        Assert.Equal("scripts", types.Default);
        Assert.Null(types.Warning);
    }

    [Fact]
    public void CustomYamlIsRead()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(YamlPath, "default: reports\ntypes:\n  - reports\n  - migrations\n");

        var types = ScriptTypes.Load(YamlPath);

        Assert.Equal(new[] { "reports", "migrations" }, types.Names);
        Assert.Equal("reports", types.Default);
    }

    [Fact]
    public void DefaultMissingFromTheListIsAddedSoEveryScriptHasAType()
    {
        var types = new ScriptTypes("scripts", new[] { "cleanup", "analytics" });

        Assert.Equal("scripts", types.Default);
        Assert.Contains("scripts", types.Names);
    }

    [Fact]
    public void BlankAndDuplicateNamesAreDropped()
    {
        var types = new ScriptTypes(null, new[] { "a", " ", "A", "b" });

        Assert.Equal(new[] { "a", "b" }, types.Names);
        Assert.Equal("a", types.Default);
    }

    [Fact]
    public void DamagedYamlFallsBackToBuiltInTypesWithAWarning()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(YamlPath, "types: [unclosed");

        var types = ScriptTypes.Load(YamlPath);

        Assert.NotNull(types.Warning);
        Assert.Contains("scripts", types.Names);
    }

    [Fact]
    public void EmptyListFallsBackToBuiltInTypes()
    {
        var types = new ScriptTypes(null, Array.Empty<string>());

        Assert.Contains("troubleshooting", types.Names);
    }
}
