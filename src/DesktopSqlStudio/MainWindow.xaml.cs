using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopSqlStudio.Core;

namespace DesktopSqlStudio;

/// <summary>WPF front end. All logic lives in <see cref="QueryWorkspace"/>; this class only draws it.</summary>
public partial class MainWindow : Window
{
    private sealed record NodeInfo(string Key, CatalogTag Tag);

    private readonly QueryWorkspace _ws;
    private bool _syncing;
    private bool _initialExpandDone;
    private CatalogTag? _context; // the server/database/type node last clicked in the tree: where "New Query" goes
    private readonly HashSet<string> _expandedKeys = new();
    private readonly Dictionary<string, TreeViewItem> _scriptItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToggleButton> _typeButtons = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(QueryWorkspace workspace)
    {
        InitializeComponent();
        _ws = workspace;

        BuildTypeButtons();
        foreach (var document in _ws.Documents.ToList()) AddTab(document);
        if (Tabs.Items.Count == 0) NewQuery();
        Tabs.SelectedIndex = 0;
        BuildTree();

        StatusText.Text = _ws.ConfigWarnings() ?? (_ws.RestoredCount > 0
            ? $"Restored {_ws.RestoredCount} script(s) from {_ws.Store.Folder}"
            : $"Scripts are saved under {_ws.Store.Folder}");
    }

    private QueryView? Current => (Tabs.SelectedItem as TabItem)?.Content as QueryView;

    private IEnumerable<QueryView> Views => Tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<QueryView>();

    // ---------- tabs ----------

    private void NewQuery()
    {
        var document = _ws.NewDocument(Current?.Document, _context);
        var view = AddTab(document);
        SafeSave(() => { _ws.Save(document); _ws.SaveSession(); }); // the file exists from the first moment
        Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().Last();
        BuildTree();
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

    private void RefreshTitle(QueryView view)
    {
        var tab = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => ReferenceEquals(t.Content, view));
        if (tab?.Header is StackPanel panel && panel.Children[0] is TextBlock title) title.Text = view.Document.Title;
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
        BuildTree();
    }

    // ---------- autosave ----------

    private void SaveSession() => SafeSave(_ws.SaveSession);

    /// <summary>Writes every open script and the session list to disk right now.</summary>
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
            StatusText.Text = $"SAVE FAILED: {ex.Message}";
        }
    }

    // ---------- tree: servers > databases > types > scripts ----------

    private TreeViewItem MakeItem(string key, string header, CatalogTag tag, bool bold, bool expandByDefault)
    {
        if (!_initialExpandDone && expandByDefault) _expandedKeys.Add(key);
        var item = new TreeViewItem
        {
            Header = header,
            Tag = new NodeInfo(key, tag),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            IsExpanded = _expandedKeys.Contains(key)
        };
        // Remember what the user expanded so a rebuild keeps the tree as it was.
        item.Expanded += (_, e) => { _expandedKeys.Add(key); e.Handled = true; };
        item.Collapsed += (_, e) => { _expandedKeys.Remove(key); e.Handled = true; };
        return item;
    }

    /// <summary>Makes sure the tree is open down to the given script so it can be selected.</summary>
    private void RevealInTree(QueryDocument document)
    {
        if (document.Server is null)
        {
            _expandedKeys.Add("G:_unassigned");
            _expandedKeys.Add($"T:_unassigned/{document.Type}");
            return;
        }
        var group = _ws.Config.ServerGroups.FirstOrDefault(g => g.Servers.Any(s => s.Name.Equals(document.Server, StringComparison.OrdinalIgnoreCase)));
        if (group is not null) _expandedKeys.Add($"G:{group.Name}");
        _expandedKeys.Add($"S:{document.Server}");
        _expandedKeys.Add($"D:{document.Server}/{document.Database}");
        _expandedKeys.Add($"T:{document.Server}/{document.Database}/{document.Type}");
    }

    private void AddScripts(TreeViewItem typeItem, ServerEntry? server, string? database, TypeFolder type)
    {
        foreach (var script in type.Scripts)
        {
            var item = MakeItem($"F:{script.Path}", script.Name, new CatalogTag(server, database, type.Type, script.Path), bold: false, expandByDefault: false);
            typeItem.Items.Add(item);
            _scriptItems[script.Path] = item;
        }
    }

    private void BuildTree()
    {
        _syncing = true;
        if (Current is not null) RevealInTree(Current.Document);
        ServerTree.Items.Clear();
        _scriptItems.Clear();

        var catalog = _ws.BuildCatalog();
        foreach (var group in catalog.Groups)
        {
            var groupItem = MakeItem($"G:{group.Name}", group.Name, new CatalogTag(null, null, null, null), bold: true, expandByDefault: true);
            foreach (var serverFolder in group.Servers)
            {
                var server = serverFolder.Server;
                var serverItem = MakeItem($"S:{server.Name}", server.Name, new CatalogTag(server, null, null, null), bold: true, expandByDefault: true);
                foreach (var db in serverFolder.Databases)
                {
                    var dbItem = MakeItem($"D:{server.Name}/{db.Database}", db.Database, new CatalogTag(server, db.Database, null, null), bold: false, expandByDefault: false);
                    foreach (var type in db.Types)
                    {
                        var typeItem = MakeItem($"T:{server.Name}/{db.Database}/{type.Type}", $"{type.Type} ({type.Scripts.Count})", new CatalogTag(server, db.Database, type.Type, null), bold: false, expandByDefault: false);
                        AddScripts(typeItem, server, db.Database, type);
                        dbItem.Items.Add(typeItem);
                    }
                    serverItem.Items.Add(dbItem);
                }
                groupItem.Items.Add(serverItem);
            }
            ServerTree.Items.Add(groupItem);
        }

        if (catalog.Unassigned.Count > 0)
        {
            var root = MakeItem("G:_unassigned", "Unassigned (no server yet)", new CatalogTag(null, null, null, null), bold: true, expandByDefault: false);
            foreach (var type in catalog.Unassigned)
            {
                var typeItem = MakeItem($"T:_unassigned/{type.Type}", $"{type.Type} ({type.Scripts.Count})", new CatalogTag(null, null, type.Type, null), bold: false, expandByDefault: false);
                AddScripts(typeItem, null, null, type);
                root.Items.Add(typeItem);
            }
            ServerTree.Items.Add(root);
        }

        if (ServerTree.Items.Count == 0)
            ServerTree.Items.Add(new TreeViewItem { Header = "No servers: File > Edit Config", IsEnabled = false });

        _initialExpandDone = true;
        _syncing = false;
        SyncPickers();
    }

    private void ServerTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncing || e.NewValue is not TreeViewItem { Tag: NodeInfo info }) return;

        if (info.Tag.ScriptPath is not null)
        {
            OpenFromTree(info.Tag);
        }
        else
        {
            _context = info.Tag; // server / database / type node: new scripts go here
            var where = info.Tag.Server is null ? "Unassigned" : $"{info.Tag.Server.Name} / {info.Tag.Database ?? info.Tag.Server.DefaultDatabase} / {info.Tag.Type ?? _ws.Types.Default}";
            StatusText.Text = $"New scripts will be created in: {where}";
        }
    }

    private void OpenFromTree(CatalogTag tag)
    {
        QueryDocument? document;
        try { document = _ws.OpenScript(tag); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not open the script: {ex.Message}";
            return;
        }
        if (document is null) return;

        var tab = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Content is QueryView v && ReferenceEquals(v.Document, document));
        if (tab is null)
        {
            AddTab(document);
            tab = Tabs.Items.OfType<TabItem>().Last();
            SaveSession();
        }
        Tabs.SelectedItem = tab;
    }

    // ---------- pickers: server, database, type ----------

    private void BuildTypeButtons()
    {
        TypePanel.Children.Clear();
        _typeButtons.Clear();
        foreach (var name in _ws.Types.Names)
        {
            var button = new ToggleButton { Content = name, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 2, 0) };
            var type = name;
            button.Click += (_, _) => Retarget(d => _ws.SetType(d, type));
            TypePanel.Children.Add(button);
            _typeButtons[name] = button;
        }
    }

    /// <summary>Makes the toolbar and tree selection match the current script.</summary>
    private void SyncPickers()
    {
        _syncing = true;
        var document = Current?.Document;
        var server = _ws.ServerOf(document);

        ServerCombo.ItemsSource = _ws.Config.AllServers().ToList();
        ServerCombo.SelectedItem = server;
        DatabaseCombo.ItemsSource = server?.Databases;
        DatabaseCombo.SelectedItem = server?.Databases.FirstOrDefault(d => d.Equals(document?.Database, StringComparison.OrdinalIgnoreCase));
        foreach (var (name, button) in _typeButtons)
            button.IsChecked = document is not null && name.Equals(document.Type, StringComparison.OrdinalIgnoreCase);

        if (ServerTree.SelectedItem is TreeViewItem selected) selected.IsSelected = false;
        if (document?.FilePath is { } path && _scriptItems.TryGetValue(path, out var item))
        {
            item.IsSelected = true;
            item.BringIntoView();
        }
        _syncing = false;
        UpdateButtons();
    }

    /// <summary>Applies a server/database/type change to the current script: moves its file, then refreshes everything that shows the path.</summary>
    private void Retarget(Action<QueryDocument> change)
    {
        if (_syncing || Current is not { } view) return;
        view.SyncText();
        SafeSave(() => change(view.Document));
        RefreshTitle(view);
        SaveSession();
        BuildTree();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        _context = null;
        if (Current is not null) RevealInTree(Current.Document);
        BuildTree();
        Current?.FocusEditor();
    }

    private void ServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ServerCombo.SelectedItem is not ServerEntry server) return;
        Retarget(d => _ws.SelectServer(d, server));
    }

    private void DatabaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || DatabaseCombo.SelectedItem is not string database) return;
        Retarget(d => _ws.SetDatabase(d, database));
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

    private void EditTypes_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_ws.TypesPath}\"") { UseShellExecute = true });

    private void ReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ws.ReloadConfig();
            BuildTypeButtons();
            BuildTree();
            StatusText.Text = _ws.ConfigWarnings() ??
                $"Config reloaded: {_ws.Config.AllServers().Count()} server(s) in {_ws.Config.ServerGroups.Count} group(s), {_ws.Types.Names.Count} script type(s). Scripts folder changes need a restart.";
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
