using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopSqlStudio.Core;

namespace DesktopSqlStudio;

public partial class MainWindow : Window
{
    private AppConfig _config;
    private readonly string _configPath;
    private readonly QueryStore _store;
    private bool _syncingCombo;

    public MainWindow(AppConfig config, string configPath, QueryStore store)
    {
        InitializeComponent();
        _config = config;
        _configPath = configPath;
        _store = store;

        ConnectionCombo.ItemsSource = _config.Connections;

        var restored = _store.LoadOpen();
        foreach (var q in restored) AddTab(q);
        if (Tabs.Items.Count == 0) NewQuery();
        Tabs.SelectedIndex = 0;

        StatusText.Text = restored.Count > 0
            ? $"Restored {restored.Count} query window(s) from {_store.Folder}"
            : $"Autosaving to {_store.Folder}";
    }

    private QueryView? Current => (Tabs.SelectedItem as TabItem)?.Content as QueryView;

    // ---------- tabs ----------

    private void NewQuery()
    {
        var number = Tabs.Items.OfType<TabItem>()
            .Select(t => (t.Content as QueryView)?.Title ?? "")
            .Select(title => Regex.Match(title, @"^Query (\d+)$"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(0).Max() + 1;

        var saved = new SavedQuery
        {
            Id = QueryStore.NewId(),
            Title = $"Query {number}",
            Connection = (ConnectionCombo.SelectedItem as ConnectionProfile)?.Name
        };
        var view = AddTab(saved);
        SafeSave(() => _store.SaveText(view.Id, view.Text)); // the file exists from the first moment
        Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().Last();
        SaveSession();
        view.FocusEditor();
    }

    private QueryView AddTab(SavedQuery saved)
    {
        var view = new QueryView(saved);
        view.Changed += (_, _) => SafeSave(() => _store.SaveText(view.Id, view.Text));
        view.RunningChanged += (_, _) => UpdateButtons();

        var closeButton = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(8, 0, 0, 0), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = view.Title, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(closeButton);

        var tab = new TabItem { Header = header, Content = view };
        closeButton.Click += (_, _) => CloseTab(tab);
        Tabs.Items.Add(tab);
        return view;
    }

    private void CloseTab(TabItem tab)
    {
        if (tab.Content is not QueryView view) return;
        view.Cancel();
        // Save the final text, then move the file to Closed/ (never deleted).
        SafeSave(() => { _store.SaveText(view.Id, view.Text); _store.MarkClosed(view.Id); });
        Tabs.Items.Remove(tab);
        if (Tabs.Items.Count == 0) NewQuery();
        SaveSession();
    }

    // ---------- autosave ----------

    private void SaveSession() => SafeSave(() => _store.SaveSession(OpenQueries()));

    private IEnumerable<SavedQuery> OpenQueries() =>
        Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<QueryView>()
            .Select(v => new SavedQuery { Id = v.Id, Title = v.Title, Connection = v.ConnectionName, Text = v.Text });

    /// <summary>Writes every open query and the session list to disk right now.</summary>
    public void FlushAll()
    {
        foreach (var q in OpenQueries()) _store.SaveText(q.Id, q.Text);
        _store.SaveSession(OpenQueries());
    }

    private void SafeSave(Action save)
    {
        try { save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"AUTOSAVE FAILED: {ex.Message}";
        }
    }

    // ---------- connections ----------

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        _syncingCombo = true;
        ConnectionCombo.SelectedItem = _config.Connections.FirstOrDefault(c => c.Name == Current?.ConnectionName);
        _syncingCombo = false;
        UpdateButtons();
        Current?.FocusEditor();
    }

    private void ConnectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCombo || Current is null) return;
        Current.ConnectionName = (ConnectionCombo.SelectedItem as ConnectionProfile)?.Name;
        SaveSession();
    }

    private void UpdateButtons()
    {
        var running = Current?.IsRunning == true;
        ExecuteButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
    }

    // ---------- commands ----------

    private async void Execute_Click(object sender, RoutedEventArgs e) => await ExecuteAsync();

    private async Task ExecuteAsync()
    {
        var view = Current;
        if (view is null || view.IsRunning) return;

        if (ConnectionCombo.SelectedItem is not ConnectionProfile profile)
        {
            StatusText.Text = _config.Connections.Count == 0
                ? "No connections yet: File > Edit Config, add one, then File > Reload Config."
                : "Pick a connection first.";
            return;
        }

        // Make sure what we are about to run is already safely on disk.
        SafeSave(() => _store.SaveText(view.Id, view.Text));
        StatusText.Text = $"Running on {profile.Name}...";
        await view.RunAsync(profile);
        StatusText.Text = $"Finished on {profile.Name}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Current?.Cancel();
    private void NewQuery_Click(object sender, RoutedEventArgs e) => NewQuery();
    private void CloseQuery_Click(object sender, RoutedEventArgs e) { if (Tabs.SelectedItem is TabItem t) CloseTab(t); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenAutosaveFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.Folder}\"") { UseShellExecute = true });

    private void EditConfig_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_configPath}\"") { UseShellExecute = true });

    private void ReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _config = AppConfig.Load(_configPath);
            _syncingCombo = true;
            ConnectionCombo.ItemsSource = _config.Connections;
            ConnectionCombo.SelectedItem = _config.Connections.FirstOrDefault(c => c.Name == Current?.ConnectionName);
            _syncingCombo = false;
            StatusText.Text = $"Config reloaded: {_config.Connections.Count} connection(s). Autosave folder changes need a restart.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Config error: {ex.Message}";
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (e.Key == Key.F5 || (ctrl && e.Key == Key.E)) { e.Handled = true; await ExecuteAsync(); }
        else if (ctrl && e.Key == Key.N) { e.Handled = true; NewQuery(); }
        else if (ctrl && e.Key == Key.W) { e.Handled = true; if (Tabs.SelectedItem is TabItem t) CloseTab(t); }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        foreach (var view in Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<QueryView>()) view.Cancel();
        try { FlushAll(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            e.Cancel = MessageBox.Show($"Could not save your queries:\n\n{ex.Message}\n\nClose anyway?", "Desktop SQL Studio",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No;
        }
    }
}
