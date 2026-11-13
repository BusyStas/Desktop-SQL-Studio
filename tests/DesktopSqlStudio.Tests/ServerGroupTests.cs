using DesktopSqlStudio.Core;
using Xunit;

namespace DesktopSqlStudio.Tests;

public class ServerGroupTests
{
    private static AppConfig Sample()
    {
        var config = new AppConfig();
        config.Credentials.Add(new CredentialEntry { Name = "SqlDev", Auth = AuthType.Sql, UserName = "u", Password = "p" });
        config.ServerGroups.Add(new ServerGroup
        {
            Name = "Dev",
            Servers =
            {
                new ServerEntry { Name = "dev-1", Address = "host1", Credential = "SqlDev", DefaultDatabase = "Main", Databases = { "Sales", "Main", "sales" } }
            }
        });
        config.Normalize();
        return config;
    }

    [Fact]
    public void DefaultDatabaseIsFirstAndListHasNoDuplicates()
    {
        var server = Sample().FindServer("dev-1")!;
        Assert.Equal(new[] { "Main", "Sales" }, server.Databases);
    }

    [Fact]
    public void ResolveCombinesServerDatabaseAndCredential()
    {
        Assert.True(Sample().TryResolve("dev-1", "Sales", out var profile, out _));
        Assert.Equal("host1", profile.Server);
        Assert.Equal("Sales", profile.Database);
        Assert.Equal("u", profile.UserName);
    }

    [Fact]
    public void ResolveFallsBackToDefaultDatabase()
    {
        Assert.True(Sample().TryResolve("dev-1", null, out var profile, out _));
        Assert.Equal("Main", profile.Database);
    }

    [Fact]
    public void ResolveFailsForUnknownServerOrCredential()
    {
        var config = Sample();
        Assert.False(config.TryResolve("nope", null, out _, out _));

        config.FindServer("dev-1")!.Credential = "Missing";
        Assert.False(config.TryResolve("dev-1", null, out _, out var error));
        Assert.Contains("Missing", error);
        Assert.Contains(config.Validate(), p => p.Contains("Missing"));
    }

    [Fact]
    public void ValidateReportsDuplicateServerNames()
    {
        var config = Sample();
        config.ServerGroups.Add(new ServerGroup { Name = "Prod", Servers = { new ServerEntry { Name = "DEV-1", Credential = "SqlDev" } } });
        Assert.Contains(config.Validate(), p => p.Contains("more than once"));
    }
}
