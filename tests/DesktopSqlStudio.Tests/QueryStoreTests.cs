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

    [Fact]
    public void SavedQueriesComeBackInSessionOrder()
    {
        var store = new QueryStore(_folder);
        store.SaveText("b", "SELECT 2");
        store.SaveText("a", "SELECT 1");
        store.SaveSession(new[]
        {
            new SavedQuery { Id = "b", Title = "Second", Connection = "Dev" },
            new SavedQuery { Id = "a", Title = "First" }
        });

        var loaded = new QueryStore(_folder).LoadOpen();

        Assert.Equal(new[] { "b", "a" }, loaded.Select(q => q.Id));
        Assert.Equal("SELECT 2", loaded[0].Text);
        Assert.Equal("Dev", loaded[0].Connection);
    }

    [Fact]
    public void UnlistedFilesAreRecoveredAfterACrash()
    {
        var store = new QueryStore(_folder);
        store.SaveText("lost", "SELECT 'never lost'");

        var loaded = store.LoadOpen();

        Assert.Single(loaded);
        Assert.Equal("SELECT 'never lost'", loaded[0].Text);
    }

    [Fact]
    public void DamagedSessionFileStillRecoversQueries()
    {
        var store = new QueryStore(_folder);
        store.SaveText("a", "SELECT 1");
        File.WriteAllText(Path.Combine(_folder, "session.json"), "{ not json");

        Assert.Single(store.LoadOpen());
    }

    [Fact]
    public void ClosedQueriesAreMovedNotDeleted()
    {
        var store = new QueryStore(_folder);
        store.SaveText("a", "SELECT 1");

        store.MarkClosed("a");

        Assert.Empty(store.LoadOpen());
        Assert.True(File.Exists(Path.Combine(_folder, "Closed", "a.sql")));
    }

    [Fact]
    public void ConfigRoundTripsWithPlainPasswords()
    {
        var path = Path.Combine(_folder, "config.json");
        Directory.CreateDirectory(_folder);
        var config = new AppConfig();
        config.Connections.Add(new ConnectionProfile { Name = "Dev", Server = "srv", Auth = AuthType.WindowsAs, WindowsUser = "bob", WindowsPassword = "pw" });
        config.Save(path);

        var loaded = AppConfig.Load(path);

        Assert.Equal(AuthType.WindowsAs, loaded.Connections[0].Auth);
        Assert.Equal("pw", loaded.Connections[0].WindowsPassword);
    }
}
