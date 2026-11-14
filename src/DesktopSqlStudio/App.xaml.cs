using System.Windows;
using DesktopSqlStudio.Core;

namespace DesktopSqlStudio;

public partial class App : Application
{
    private MainWindow? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configPath = AppConfig.DefaultConfigPath();
        AppConfig config;
        QueryStore store;
        try
        {
            config = AppConfig.Load(configPath);
            store = new QueryStore(config.AutosaveFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not start:\n\n{ex.Message}\n\nConfig file: {configPath}", "Desktop SQL Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Last line of defence: if anything blows up, write every open query to disk first.
        DispatcherUnhandledException += (_, _) => SafeFlush();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => SafeFlush();

        _main = new MainWindow(new QueryWorkspace(config, configPath, store));
        _main.Show();
    }

    private void SafeFlush()
    {
        try { _main?.FlushAll(); } catch { /* nothing more we can do while crashing */ }
    }
}
