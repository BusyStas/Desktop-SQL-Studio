using DesktopSqlStudio.Core;
using Xunit;

namespace DesktopSqlStudio.Tests;

public class QueryWorkspaceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dss-ws-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private QueryWorkspace Create()
    {
        var config = new AppConfig();
        config.Credentials.Add(new CredentialEntry { Name = "c", Auth = AuthType.Windows });
        config.ServerGroups.Add(new ServerGroup
        {
            Name = "G",
            Servers =
            {
                new ServerEntry { Name = "s1", Address = "a", Credential = "c", DefaultDatabase = "Main", Databases = { "Sales" } },
                new ServerEntry { Name = "s2", Address = "b", Credential = "c", DefaultDatabase = "Other" }
            }
        });
        config.Normalize();
        return new QueryWorkspace(config, Path.Combine(_folder, "config.json"), new QueryStore(_folder));
    }

    [Fact]
    public void NewDocumentsAreNumberedAndInheritServerAndDatabase()
    {
        var ws = Create();
        var first = ws.NewDocument(null);
        first.Server = "s1";
        first.Database = "Sales";

        var second = ws.NewDocument(first);

        Assert.Equal("Query 1", first.Title);
        Assert.Equal("Query 2", second.Title);
        Assert.Equal("s1", second.Server);
        Assert.Equal("Sales", second.Database);
    }

    [Fact]
    public void SelectServerKeepsDatabaseOnlyWhenTheServerHasIt()
    {
        var ws = Create();
        var doc = ws.NewDocument(null);
        doc.Database = "Sales";

        ws.SelectServer(doc, ws.Config.FindServer("s1")!);
        Assert.Equal("Sales", doc.Database);

        ws.SelectServer(doc, ws.Config.FindServer("s2")!);
        Assert.Equal("Other", doc.Database);
        Assert.Equal("s2", doc.Server);
    }

    [Fact]
    public void FlushAllMakesWindowsSurviveARestart()
    {
        var ws = Create();
        var doc = ws.NewDocument(null);
        doc.Text = "SELECT 42";
        doc.Server = "s1";
        ws.FlushAll();

        var restarted = Create();

        Assert.Equal(1, restarted.RestoredCount);
        Assert.Equal("SELECT 42", restarted.Documents[0].Text);
        Assert.Equal("s1", restarted.Documents[0].Server);
    }

    [Fact]
    public void ClosingAWindowMovesItsFileAndForgetsIt()
    {
        var ws = Create();
        var doc = ws.NewDocument(null);
        doc.Text = "SELECT 1";

        ws.Close(doc);

        Assert.Empty(ws.Documents);
        Assert.True(File.Exists(Path.Combine(_folder, "Closed", doc.Id + ".sql")));
    }

    [Fact]
    public async Task RunWithoutAServerReturnsAFailedResultInsteadOfConnecting()
    {
        var ws = Create();
        var doc = ws.NewDocument(null);

        var result = await ws.RunAsync(doc, "SELECT 1", CancellationToken.None);

        Assert.True(result.Failed);
        Assert.Single(result.Messages);
    }
}
