using System.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Data.SqlClient;
using Microsoft.Win32.SafeHandles;

namespace DesktopSqlStudio.Core;

public sealed class QueryResult
{
    public List<DataTable> Tables { get; } = new();
    public List<string> Messages { get; } = new();
    public TimeSpan Elapsed { get; set; }
    public bool Failed { get; set; }
}

public static class QueryExecutor
{
    /// <summary>Safety cap per result set so a huge SELECT cannot exhaust memory.</summary>
    public const int MaxRowsPerResult = 100_000;

    public static string BuildConnectionString(ConnectionProfile p)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = p.Server,
            InitialCatalog = p.Database,
            ApplicationName = "Desktop-SQL-Studio",
            TrustServerCertificate = p.TrustServerCertificate,
            Encrypt = SqlConnectionEncryptOption.Mandatory
        };
        if (p.Auth == AuthType.Sql)
        {
            b.UserID = p.UserName ?? "";
            b.Password = p.Password ?? "";
        }
        else
        {
            b.IntegratedSecurity = true;
        }
        return b.ConnectionString;
    }

    public static async Task<QueryResult> ExecuteAsync(ConnectionProfile profile, string script, CancellationToken ct)
    {
        if (profile.Auth == AuthType.WindowsAs)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Running as another Windows user requires Windows.");
            return await RunAsWindowsUserAsync(profile, script, ct);
        }
        return await RunAsync(profile, script, ct);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<QueryResult> RunAsWindowsUserAsync(ConnectionProfile p, string script, CancellationToken ct)
    {
        // NEW_CREDENTIALS: the process identity stays the same locally, but network
        // authentication (to SQL Server) uses the supplied account. Works for non-domain-joined machines too.
        const int Logon32LogonNewCredentials = 9;
        const int Logon32ProviderWinnt50 = 3;

        if (!LogonUser(p.WindowsUser ?? "", p.WindowsDomain ?? ".", p.WindowsPassword ?? "",
                Logon32LogonNewCredentials, Logon32ProviderWinnt50, out var token))
        {
            throw new InvalidOperationException($"Windows logon failed for {p.WindowsDomain}\\{p.WindowsUser} (error {Marshal.GetLastWin32Error()}).");
        }
        using (token)
        {
            return await WindowsIdentity.RunImpersonatedAsync(token, () => RunAsync(p, script, ct));
        }
    }

    private static async Task<QueryResult> RunAsync(ConnectionProfile profile, string script, CancellationToken ct)
    {
        var result = new QueryResult();
        var clock = Stopwatch.StartNew();
        try
        {
            await using var connection = new SqlConnection(BuildConnectionString(profile));
            connection.InfoMessage += (_, e) => result.Messages.Add(e.Message);
            await connection.OpenAsync(ct);

            foreach (var batch in BatchSplitter.Split(script))
            {
                await using var command = new SqlCommand(batch, connection) { CommandTimeout = profile.CommandTimeoutSeconds };
                await using var reader = await command.ExecuteReaderAsync(ct);
                do
                {
                    if (reader.FieldCount > 0) result.Tables.Add(await ReadTableAsync(reader, result, ct));
                } while (await reader.NextResultAsync(ct));

                if (reader.RecordsAffected >= 0) result.Messages.Add($"({reader.RecordsAffected} row(s) affected)");
            }
        }
        catch (OperationCanceledException)
        {
            result.Failed = true;
            result.Messages.Add("Query cancelled.");
        }
        catch (SqlException ex)
        {
            result.Failed = true;
            result.Messages.Add($"Msg {ex.Number}, Level {ex.Class}, State {ex.State}, Line {ex.LineNumber}: {ex.Message}");
        }
        catch (Exception ex)
        {
            result.Failed = true;
            result.Messages.Add(ex.Message);
        }
        result.Elapsed = clock.Elapsed;
        return result;
    }

    private static async Task<DataTable> ReadTableAsync(SqlDataReader reader, QueryResult result, CancellationToken ct)
    {
        var table = new DataTable();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (string.IsNullOrEmpty(name)) name = "(No column name)";
            var unique = name;
            for (var n = 2; !used.Add(unique); n++) unique = $"{name}_{n}";
            table.Columns.Add(unique, typeof(object));
        }

        var values = new object[reader.FieldCount];
        var truncated = false;
        while (await reader.ReadAsync(ct))
        {
            if (table.Rows.Count >= MaxRowsPerResult)
            {
                truncated = true;
                break; // remaining rows of this result set are skipped by NextResultAsync
            }
            reader.GetValues(values);
            table.Rows.Add(values);
        }
        if (truncated) result.Messages.Add($"Result set truncated at {MaxRowsPerResult:N0} rows.");
        return table;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [SupportedOSPlatform("windows")]
    private static extern bool LogonUser(string user, string domain, string password, int logonType, int logonProvider, out SafeAccessTokenHandle token);
}
