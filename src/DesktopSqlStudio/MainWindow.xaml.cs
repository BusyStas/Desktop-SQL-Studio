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
    private bool _syncing;
    private readonly Dictionary<string, TreeViewItem> _serverItems = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(AppConfig config, string configPath, QueryStore store)
    {
        InitializeComponent();
        _config = config;
        _configPath = configPath;
        _store = store;

        BuildTree();

        var restored = _store.LoadOpen();
        foreach (var q in restored) AddTab(q);
        if (Tabs.Items.Count == 0) NewQuery();
        Tabs.SelectedIndex = 0;

        StatusText.Text = ConfigWarnings() ?? (restored.Count > 0
            ? $"Restored {restored.Count} query window(s) from {_store.Folder}"
            : $"Autosaving to {_store.Folder}");
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
            Server = Current?.ServerName,
            Database = Current?.Database
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
            .Select(v => new SavedQuery { Id = v.Id, Title = v.Title, Server = v.ServerName, Database = v.Database, Text = v.Text });

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

    // ---------- servers and databases ----------

    private string? ConfigWarnings()
    {
        var problems = _config.Validate();
        return problems.Count == 0 ? null : "Config: " + string.Join(" ", problems);
    }

    private void BuildTree()
    {
        _syncing = true;
        ServerTree.Items.Clear();
        _serverItems.Clear();
        foreach (var group in _config.ServerGroups)
        {
            var groupItem = new TreeViewItem { Header = group.Name, IsExpanded = true, FontWeight = FontWeights.SemiBold };
            foreach (var server in group.Servers)
            {
                var item = new TreeViewItem { Header = server.Name, Tag = server, FontWeight = FontWeights.Normal };
                groupItem.Items.Add(item);
                _serverItems.TryAdd(server.Name, item);
            }
            ServerTree.Items.Add(groupItem);
        }
        _syncing = false;
        SyncPickers();
    }

    /// <summary>Makes the tree selection, server label and database dropdown match the current query window.</summary>
    private void SyncPickers()
    {
        _syncing = true;
        var view = Current;
        var server = _config.FindServer(view?.ServerName);

        if (ServerTree.SelectedItem is TreeViewItem selected) selected.IsSelected = false;
        if (server is not null && _serverItems.TryGetValue(server.Name, out var item)) item.IsSelected = true;

        ServerLabel.Text = server?.Name ?? "(none)";
        DatabaseCombo.ItemsSource = server?.Databases;
        DatabaseCombo.SelectedItem = server?.Databases.FirstOrDefault(d => d.Equals(view?.Database, StringComparison.OrdinalIgnoreCase));
        _syncing = false;
        UpdateButtons();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        SyncPickers();
        Current?.FocusEditor();
    }

    private void ServerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncing || Current is null) return;
        if (e.NewValue is not TreeViewItem { Tag: ServerEntry server }) return; // a group row: ignore

        Current.ServerName = server.Name;
        if (!server.Databases.Contains(Current.Database ?? "", StringComparer.OrdinalIgnoreCase))
            Current.Database = server.DefaultDatabase;
        SyncPickers();
        SaveSession();
    }

    private void DatabaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Current is null || DatabaseCombo.SelectedItem is not string database) return;
        Current.Database = database;
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

        if (!_config.TryResolve(view.ServerName, view.Database, out var profile, out var error))
        {
            StatusText.Text = _config.ServerGroups.Count == 0
                ? "No servers yet: File > Edit Config, add a credential and a server group, then File > Reload Config."
                : error;
            return;
        }

        // Make sure what we are about to run is already safely on disk.
        SafeSave(() => _store.SaveText(view.Id, view.Text));
        StatusText.Text = $"Running on {profile.Name} / {profile.Database}...";
        await view.RunAsync(profile);
        StatusText.Text = $"Finished on {profile.Name} / {profile.Database}";
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
            BuildTree();
            StatusText.Text = ConfigWarnings() ??
                $"Config reloaded: {_config.AllServers().Count()} server(s) in {_config.ServerGroups.Count} group(s). Autosave folder changes need a restart.";
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
