using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopSqlStudio.Core;

namespace DesktopSqlStudio;

/// <summary>WPF front end. All logic lives in <see cref="QueryWorkspace"/>; this class only draws it.</summary>
public partial class MainWindow : Window
{
    private readonly QueryWorkspace _ws;
    private bool _syncing;
    private readonly Dictionary<string, TreeViewItem> _serverItems = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(QueryWorkspace workspace)
    {
        InitializeComponent();
        _ws = workspace;

        BuildTree();
        foreach (var document in _ws.Documents.ToList()) AddTab(document);
        if (Tabs.Items.Count == 0) NewQuery();
        Tabs.SelectedIndex = 0;

        StatusText.Text = _ws.ConfigWarnings() ?? (_ws.RestoredCount > 0
            ? $"Restored {_ws.RestoredCount} query window(s) from {_ws.Store.Folder}"
            : $"Autosaving to {_ws.Store.Folder}");
    }

    private QueryView? Current => (Tabs.SelectedItem as TabItem)?.Content as QueryView;

    private IEnumerable<QueryView> Views => Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<QueryView>();

    // ---------- tabs ----------

    private void NewQuery()
    {
        var document = _ws.NewDocument(Current?.Document);
        var view = AddTab(document);
        SafeSave(() => { _ws.Save(document); _ws.SaveSession(); }); // the file exists from the first moment
        Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().Last();
        view.FocusEditor();
    }

    private QueryView AddTab(QueryDocument document)
    {
        var view = new QueryView(document);
        view.Changed += (_, _) => SafeSave(() => _ws.Save(document));
        view.RunningChanged += (_, _) => UpdateButtons();

        var closeButton = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(8, 0, 0, 0), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = document.Title, VerticalAlignment = VerticalAlignment.Center });
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
        view.SyncText();
        SafeSave(() => _ws.Close(view.Document));
        Tabs.Items.Remove(tab);
        if (Tabs.Items.Count == 0) NewQuery();
        SaveSession();
    }

    // ---------- autosave ----------

    private void SaveSession() => SafeSave(_ws.SaveSession);

    /// <summary>Writes every open query and the session list to disk right now.</summary>
    public void FlushAll()
    {
        foreach (var view in Views) view.SyncText();
        _ws.FlushAll();
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

    private void BuildTree()
    {
        _syncing = true;
        ServerTree.Items.Clear();
        _serverItems.Clear();
        foreach (var group in _ws.Config.ServerGroups)
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
        if (ServerTree.Items.Count == 0)
            ServerTree.Items.Add(new TreeViewItem { Header = "No servers: File > Edit Config", IsEnabled = false });
        _syncing = false;
        SyncPickers();
    }

    /// <summary>Makes the tree selection, server label and database dropdown match the current query window.</summary>
    private void SyncPickers()
    {
        _syncing = true;
        var document = Current?.Document;
        var server = _ws.ServerOf(document);

        if (ServerTree.SelectedItem is TreeViewItem selected) selected.IsSelected = false;
        if (server is not null && _serverItems.TryGetValue(server.Name, out var item)) item.IsSelected = true;

        ServerLabel.Text = server?.Name ?? "(none)";
        DatabaseCombo.ItemsSource = server?.Databases;
        DatabaseCombo.SelectedItem = server?.Databases.FirstOrDefault(d => d.Equals(document?.Database, StringComparison.OrdinalIgnoreCase));
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

        _ws.SelectServer(Current.Document, server);
        SyncPickers();
        SaveSession();
    }

    private void DatabaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Current is null || DatabaseCombo.SelectedItem is not string database) return;
        Current.Document.Database = database;
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

        var document = view.Document;
        if (!_ws.Config.TryResolve(document.Server, document.Database, out var profile, out var error))
        {
            StatusText.Text = _ws.Config.ServerGroups.Count == 0
                ? "No servers yet: File > Edit Config, add a credential and a server group, then File > Reload Config."
                : error;
            return;
        }

        // Make sure what we are about to run is already safely on disk.
        view.SyncText();
        SafeSave(() => _ws.Save(document));
        StatusText.Text = $"Running on {profile.Name} / {profile.Database}...";
        await view.RunAsync(_ws);
        StatusText.Text = $"Finished on {profile.Name} / {profile.Database}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Current?.Cancel();
    private void NewQuery_Click(object sender, RoutedEventArgs e) => NewQuery();
    private void CloseQuery_Click(object sender, RoutedEventArgs e) { if (Tabs.SelectedItem is TabItem t) CloseTab(t); }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenAutosaveFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_ws.Store.Folder}\"") { UseShellExecute = true });

    private void EditConfig_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_ws.ConfigPath}\"") { UseShellExecute = true });

    private void ReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ws.ReloadConfig();
            BuildTree();
            StatusText.Text = _ws.ConfigWarnings() ??
                $"Config reloaded: {_ws.Config.AllServers().Count()} server(s) in {_ws.Config.ServerGroups.Count} group(s). Autosave folder changes need a restart.";
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
        foreach (var view in Views) view.Cancel();
        try { FlushAll(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            e.Cancel = MessageBox.Show($"Could not save your queries:\n\n{ex.Message}\n\nClose anyway?", "Desktop SQL Studio",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No;
        }
    }
}
