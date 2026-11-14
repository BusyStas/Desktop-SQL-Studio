using DesktopSqlStudio.Core;
using Xunit;

namespace DesktopSqlStudio.Tests;

public class QueryWorkspaceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dss-ws-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_folder, "root");

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
        var typesPath = Path.Combine(_folder, "script-types.yaml");
        return new QueryWorkspace(config, Path.Combine(_folder, "config.json"), new QueryStore(Root), ScriptTypes.Load(typesPath), typesPath);
    }

    [Fact]
    public void NewDocumentsAreNumberedAndInheritServerDatabaseAndType()
    {
        var ws = Create();
        var first = ws.NewDocument(null);
        first.Server = "s1";
        first.Database = "Sales";
        first.Type = "cleanup";

        var second = ws.NewDocument(first);

        Assert.Equal("Query 1", first.Title);
        Assert.Equal("Query 2", second.Title);
        Assert.Equal(("s1", "Sales", "cleanup"), (second.Server, second.Database, second.Type));
    }

    [Fact]
    public void NewDocumentWithoutAnythingSelectedHasTheDefaultType()
    {
        Assert.Equal("scripts", Create().NewDocument(null).Type);
    }

    [Fact]
    public void NewDocumentUsesTheSelectedTreeNode()
    {
        var ws = Create();
        var server = ws.Config.FindServer("s2")!;

        var doc = ws.NewDocument(null, new CatalogTag(server, null, null, null));

        Assert.Equal(("s2", "Other", "scripts"), (doc.Server, doc.Database, doc.Type));
        Assert.Equal(Path.Combine(Root, "s2", "Other", "scripts", "Query 1.sql"), doc.FilePath);
    }

    [Fact]
    public void NewDocumentSkipsNamesThatAlreadyExistOnDisk()
    {
        var ws = Create();
        var server = ws.Config.FindServer("s1")!;
        var dir = Path.Combine(Root, "s1", "Main", "scripts");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Query 1.sql"), "old");

        var doc = ws.NewDocument(null, new CatalogTag(server, "Main", "scripts", null));

        Assert.Equal("Query 2", doc.Title);
    }

    [Fact]
    public void ChangingTypeMovesTheScriptToTheOtherTypeFolder()
    {
        var ws = Create();
        var doc = ws.NewDocument(null, new CatalogTag(ws.Config.FindServer("s1"), "Sales", null, null));
        doc.Text = "SELECT 7";
        ws.Save(doc);

        ws.SetType(doc, "analytics");

        Assert.Equal(Path.Combine(Root, "s1", "Sales", "analytics", "Query 1.sql"), doc.FilePath);
        Assert.Equal("SELECT 7", File.ReadAllText(doc.FilePath!));
        Assert.False(File.Exists(Path.Combine(Root, "s1", "Sales", "scripts", "Query 1.sql")));
    }

    [Fact]
    public void SelectServerKeepsTheDatabaseOnlyWhenTheServerHasItAndMovesTheFile()
    {
        var ws = Create();
        var doc = ws.NewDocument(null, new CatalogTag(ws.Config.FindServer("s1"), "Sales", null, null));
        ws.Save(doc);

        ws.SelectServer(doc, ws.Config.FindServer("s2")!);

        Assert.Equal(("s2", "Other"), (doc.Server, doc.Database));
        Assert.Equal(Path.Combine(Root, "s2", "Other", "scripts", "Query 1.sql"), doc.FilePath);
    }

    [Fact]
    public void FlushAllMakesScriptsSurviveARestartWithTheirServerDatabaseAndType()
    {
        var ws = Create();
        var doc = ws.NewDocument(null, new CatalogTag(ws.Config.FindServer("s1"), "Sales", "cleanup", null));
        doc.Text = "SELECT 42";
        ws.FlushAll();

        var restarted = Create();

        Assert.Equal(1, restarted.RestoredCount);
        var restored = restarted.Documents[0];
        Assert.Equal("SELECT 42", restored.Text);
        Assert.Equal(("s1", "Sales", "cleanup"), (restored.Server, restored.Database, restored.Type));
    }

    [Fact]
    public void ClosingKeepsAScriptWithContentAndRemovesAnEmptyOne()
    {
        var ws = Create();
        var full = ws.NewDocument(null);
        full.Text = "SELECT 1";
        var empty = ws.NewDocument(null);
        ws.Save(full);
        ws.Save(empty);

        ws.Close(full);
        ws.Close(empty);

        Assert.Empty(ws.Documents);
        Assert.True(File.Exists(full.FilePath!));
        Assert.False(File.Exists(empty.FilePath!));
    }

    [Fact]
    public void OpeningAScriptTwiceReturnsTheSameWindow()
    {
        var ws = Create();
        var doc = ws.NewDocument(null, new CatalogTag(ws.Config.FindServer("s1"), "Sales", null, null));
        doc.Text = "SELECT 1";
        ws.Save(doc);
        var tag = new CatalogTag(ws.Config.FindServer("s1"), "Sales", "scripts", doc.FilePath);

        Assert.Same(doc, ws.OpenScript(tag));
        ws.Close(doc);
        var reopened = ws.OpenScript(tag);

        Assert.NotSame(doc, reopened);
        Assert.Equal("SELECT 1", reopened!.Text);
    }

    [Fact]
    public void CatalogListsConfiguredTypesEvenWhenEmptyAndExtraFoldersFromDisk()
    {
        var ws = Create();
        var doc = ws.NewDocument(null, new CatalogTag(ws.Config.FindServer("s1"), "Sales", null, null));
        doc.Text = "x";
        ws.Save(doc);
        Directory.CreateDirectory(Path.Combine(Root, "s1", "Sales", "old-stuff"));

        var catalog = ws.BuildCatalog();

        var sales = catalog.Groups[0].Servers[0].Databases.Single(d => d.Database == "Sales");
        Assert.Equal(new[] { "scripts", "troubleshooting", "cleanup", "analytics", "old-stuff" }, sales.Types.Select(t => t.Type));
        Assert.Single(sales.Types[0].Scripts);
        Assert.Empty(catalog.Unassigned);
    }

    [Fact]
    public void CatalogShowsUnassignedScriptsOnlyWhenThereAreSome()
    {
        var ws = Create();
        var doc = ws.NewDocument(null);
        doc.Text = "loose";
        ws.Save(doc);

        Assert.Single(ws.BuildCatalog().Unassigned);
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
