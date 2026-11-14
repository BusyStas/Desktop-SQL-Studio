using System.IO;
using DesktopSqlStudio.Core;

namespace DesktopSqlStudio.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var configPath = AppConfig.DefaultConfigPath();
        QueryWorkspace workspace;
        try
        {
            var config = AppConfig.Load(configPath);
            workspace = new QueryWorkspace(config, configPath, new QueryStore(config.AutosaveFolder));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not start:\n\n{ex.Message}\n\nConfig file: {configPath}", "Desktop SQL Studio",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var form = new MainForm(workspace);

        // Last line of defence: if anything blows up, write every open query to disk first.
        Application.ThreadException += (_, _) => SafeFlush(form);
        AppDomain.CurrentDomain.UnhandledException += (_, _) => SafeFlush(form);

        Application.Run(form);
    }

    private static void SafeFlush(MainForm form)
    {
        try { form.FlushAll(); } catch { /* nothing more we can do while crashing */ }
    }
}
