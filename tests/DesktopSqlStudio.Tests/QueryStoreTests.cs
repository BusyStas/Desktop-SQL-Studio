using DesktopSqlStudio.Core;
using Xunit;

namespace DesktopSqlStudio.Tests;

public class QueryStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dss-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static QueryDocument Doc(string title, string text = "SELECT 1", string type = "scripts") =>
        new() { Title = title, Server = "s1", Database = "Sales", Type = type, Text = text };

    [Fact]
    public void SaveWritesToServerDatabaseTypeFolder()
    {
        var store = new QueryStore(_folder);
        var doc = Doc("Query 1");

        store.Save(doc);

        Assert.Equal(Path.Combine(_folder, "s1", "Sales", "scripts", "Query 1.sql"), doc.FilePath);
        Assert.Equal("SELECT 1", File.ReadAllText(doc.FilePath!));
    }

    [Fact]
    public void ScriptsWithoutAServerGoToUnassigned()
    {
        var store = new QueryStore(_folder);
        var doc = new QueryDocument { Title = "Loose", Type = "cleanup", Text = "x" };

        store.Save(doc);

        Assert.Equal(Path.Combine(_folder, "_unassigned", "cleanup", "Loose.sql"), doc.FilePath);
    }

    [Fact]
    public void ChangingTheTypeMovesTheFile()
    {
        var store = new QueryStore(_folder);
        var doc = Doc("Query 1");
        store.Save(doc);
        var oldPath = doc.FilePath!;

        doc.Type = "cleanup";
        store.Relocate(doc);

        Assert.False(File.Exists(oldPath));
        Assert.Equal(Path.Combine(_folder, "s1", "Sales", "cleanup", "Query 1.sql"), doc.FilePath);
        Assert.Equal("SELECT 1", File.ReadAllText(doc.FilePath!));
    }

    [Fact]
    public void MovingOntoAnExistingNameKeepsBothFiles()
    {
        var store = new QueryStore(_folder);
        var existing = Doc("Query 1", "existing", "cleanup");
        store.Save(existing);
        var moving = Doc("Query 1", "moving");
        store.Save(moving);

        moving.Type = "cleanup";
        store.Relocate(moving);

        Assert.Equal("Query 1 (2)", moving.Title);
        Assert.Equal("existing", File.ReadAllText(existing.FilePath!));
        Assert.Equal("moving", File.ReadAllText(moving.FilePath!));
    }

    [Fact]
    public void SessionComesBackInOrderAndSkipsMissingFiles()
    {
        var store = new QueryStore(_folder);
        var a = Doc("A");
        var b = Doc("B");
        var gone = Doc("Gone");
        foreach (var d in new[] { a, b, gone }) store.Save(d);
        store.SaveSession(new[] { b.FilePath!, gone.FilePath!, a.FilePath! });
        File.Delete(gone.FilePath!);

        var loaded = new QueryStore(_folder).LoadSession();

        Assert.Equal(new[] { b.FilePath, a.FilePath }, loaded);
    }

    [Fact]
    public void DamagedOrOldFormatSessionFileIsIgnored()
    {
        var store = new QueryStore(_folder);
        File.WriteAllText(Path.Combine(_folder, "session.json"), "[{\"Id\":\"x\"}]");
        Assert.Empty(store.LoadSession());

        File.WriteAllText(Path.Combine(_folder, "session.json"), "{ not json");
        Assert.Empty(store.LoadSession());
    }

    [Fact]
    public void LooseFilesFromTheFirstVersionAreMovedIntoUnassigned()
    {
        var store = new QueryStore(_folder);
        File.WriteAllText(Path.Combine(_folder, "20261113-120000-abc123.sql"), "SELECT 'old'");

        store.MigrateLegacyFiles("scripts");

        Assert.True(File.Exists(Path.Combine(_folder, "_unassigned", "scripts", "20261113-120000-abc123.sql")));
        Assert.Empty(Directory.GetFiles(_folder, "*.sql"));
    }

    [Fact]
    public void OnlyEmptyScriptsAreDeleted()
    {
        var store = new QueryStore(_folder);
        var empty = Doc("Empty", "");
        var full = Doc("Full", "SELECT 1");
        store.Save(empty);
        store.Save(full);

        store.DeleteIfEmpty(empty);
        store.DeleteIfEmpty(full);

        Assert.False(File.Exists(empty.FilePath!));
        Assert.True(File.Exists(full.FilePath!));
    }

    [Fact]
    public void FileAndFolderNamesAreMadeSafe()
    {
        Assert.Equal("a_b", QueryStore.Sanitize("a/b"));
        Assert.Equal("_", QueryStore.Sanitize("  "));
    }

    [Fact]
    public void ConfigRoundTripsWithPlainPasswords()
    {
        var path = Path.Combine(_folder, "config.json");
        Directory.CreateDirectory(_folder);
        var config = new AppConfig();
        config.Credentials.Add(new CredentialEntry { Name = "Bob", Auth = AuthType.WindowsAs, WindowsUser = "bob", WindowsPassword = "pw" });
        config.Save(path);

        var loaded = AppConfig.Load(path);

        Assert.Equal(AuthType.WindowsAs, loaded.Credentials[0].Auth);
        Assert.Equal("pw", loaded.Credentials[0].WindowsPassword);
    }
}
