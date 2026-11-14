using System.Diagnostics;
using System.IO;
using DesktopSqlStudio.Core;

namespace DesktopSqlStudio.WinForms;

/// <summary>WinForms front end. All logic lives in <see cref="QueryWorkspace"/>; this class only draws it.</summary>
public sealed class MainForm : Form
{
    private sealed record NodeInfo(string Key, CatalogTag Tag);

    private readonly QueryWorkspace _ws;
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ToolStripComboBox _serverCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly ToolStripComboBox _databaseCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly ToolStrip _typeBar = new();
    private readonly ToolStripButton _executeButton = new("▶ Execute (F5)");
    private readonly ToolStripButton _cancelButton = new("■ Cancel") { Enabled = false };
    private readonly ToolStripStatusLabel _status = new();
    private readonly Dictionary<string, ToolStripButton> _typeButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeNode> _scriptNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedKeys = new();
    private CatalogTag? _context; // the server/database/type node last clicked in the tree: where "New Query" goes
    private bool _initialExpandDone;
    private bool _syncing;

    public MainForm(QueryWorkspace workspace)
    {
        _ws = workspace;
        Text = "Desktop SQL Studio";
        Width = 1200;
        Height = 800;
        KeyPreview = true;

        BuildLayout();
        BuildTypeButtons();
        foreach (var document in _ws.Documents.ToList()) AddPage(document);
        if (_tabs.TabPages.Count == 0) NewQuery();
        BuildTree();

        _status.Text = _ws.ConfigWarnings() ?? (_ws.RestoredCount > 0
            ? $"Restored {_ws.RestoredCount} script(s) from {_ws.Store.Folder}"
            : $"Scripts are saved under {_ws.Store.Folder}");
    }

    private QueryPage? Current => _tabs.SelectedTab as QueryPage;

    private IEnumerable<QueryPage> Pages => _tabs.TabPages.OfType<QueryPage>();

    // ---------- layout ----------

    private void BuildLayout()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 260 };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_tabs);

        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("&New Query", Keys.Control | Keys.N, (_, _) => NewQuery()));
        file.DropDownItems.Add(Item("&Close Query", Keys.Control | Keys.W, (_, _) => CloseCurrent()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Open &Scripts Folder", Keys.None, (_, _) => Open("explorer.exe", _ws.Store.Folder)));
        file.DropDownItems.Add(Item("&Edit Config (config.json)", Keys.None, (_, _) => Open("notepad.exe", _ws.ConfigPath)));
        file.DropDownItems.Add(Item("Edit Script &Types (script-types.yaml)", Keys.None, (_, _) => Open("notepad.exe", _ws.TypesPath)));
        file.DropDownItems.Add(Item("&Reload Config", Keys.None, (_, _) => ReloadConfig()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("E&xit", Keys.None, (_, _) => Close()));
        var query = new ToolStripMenuItem("&Query");
        query.DropDownItems.Add(Item("&Execute", Keys.F5, async (_, _) => await ExecuteAsync()));
        query.DropDownItems.Add(Item("&Cancel", Keys.None, (_, _) => Current?.Cancel()));
        menu.Items.Add(file);
        menu.Items.Add(query);

        var toolbar = new ToolStrip();
        toolbar.Items.Add(new ToolStripLabel("Server:"));
        toolbar.Items.Add(_serverCombo);
        toolbar.Items.Add(new ToolStripLabel("Database:"));
        toolbar.Items.Add(_databaseCombo);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_executeButton);
        toolbar.Items.Add(_cancelButton);

        var statusBar = new StatusStrip();
        statusBar.Items.Add(_status);

        // Fill first, then the edge-docked bars; the menu goes last so it ends up on top.
        Controls.Add(split);
        Controls.Add(statusBar);
        Controls.Add(_typeBar);
        Controls.Add(toolbar);
        Controls.Add(menu);
        MainMenuStrip = menu;

        _tree.AfterSelect += (_, e) => OnNodeSelected(e.Node);
        _tree.AfterExpand += (_, e) => { if (!_syncing && e.Node?.Tag is NodeInfo i) _expandedKeys.Add(i.Key); };
        _tree.AfterCollapse += (_, e) => { if (!_syncing && e.Node?.Tag is NodeInfo i) _expandedKeys.Remove(i.Key); };
        _tabs.SelectedIndexChanged += (_, _) => OnTabChanged();
        _serverCombo.SelectedIndexChanged += (_, _) => OnServerPicked();
        _databaseCombo.SelectedIndexChanged += (_, _) => OnDatabasePicked();
        _executeButton.Click += async (_, _) => await ExecuteAsync();
        _cancelButton.Click += (_, _) => Current?.Cancel();
        FormClosing += OnFormClosing;
    }

    private static ToolStripMenuItem Item(string text, Keys shortcut, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text);
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        item.Click += onClick;
        return item;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.E)) { _ = ExecuteAsync(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------- pages ----------

    private void NewQuery()
    {
        var document = _ws.NewDocument(Current?.Document, _context);
        var page = AddPage(document);
        SafeSave(() => { _ws.Save(document); _ws.SaveSession(); }); // the file exists from the first moment
        _tabs.SelectedTab = page;
        BuildTree();
        page.FocusEditor();
    }

    private QueryPage AddPage(QueryDocument document)
    {
        var page = new QueryPage(document);
        page.Changed += (_, _) => SafeSave(() => _ws.Save(document));
        page.RunningChanged += (_, _) => UpdateButtons();
        _tabs.TabPages.Add(page);
        return page;
    }

    private void CloseCurrent()
    {
        if (Current is not { } page) return;
        page.Cancel();
        page.SyncText();
        SafeSave(() => _ws.Close(page.Document));
        _tabs.TabPages.Remove(page);
        page.Dispose();
        if (_tabs.TabPages.Count == 0) NewQuery();
        SafeSave(_ws.SaveSession);
        BuildTree();
    }

    private void OnTabChanged()
    {
        _context = null;
        BuildTree();
        Current?.FocusEditor();
    }

    // ---------- autosave ----------

    /// <summary>Writes every open script and the session list to disk right now.</summary>
    public void FlushAll()
    {
        foreach (var page in Pages) page.SyncText();
        _ws.FlushAll();
    }

    private void SafeSave(Action save)
    {
        try { save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"SAVE FAILED: {ex.Message}";
        }
    }

    // ---------- tree: servers > databases > types > scripts ----------

    private TreeNode MakeNode(string key, string text, CatalogTag tag, bool bold, bool expandByDefault)
    {
        if (!_initialExpandDone && expandByDefault) _expandedKeys.Add(key);
        var node = new TreeNode(text) { Tag = new NodeInfo(key, tag) };
        if (bold) node.NodeFont = new Font(_tree.Font, FontStyle.Bold);
        return node;
    }

    private void AddScripts(TreeNode typeNode, ServerEntry? server, string? database, TypeFolder type)
    {
        foreach (var script in type.Scripts)
        {
            var node = MakeNode($"F:{script.Path}", script.Name, new CatalogTag(server, database, type.Type, script.Path), bold: false, expandByDefault: false);
            typeNode.Nodes.Add(node);
            _scriptNodes[script.Path] = node;
        }
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

    private void ApplyExpansion(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is NodeInfo info && _expandedKeys.Contains(info.Key)) node.Expand();
            ApplyExpansion(node.Nodes);
        }
    }

    private void BuildTree()
    {
        _syncing = true;
        _tree.BeginUpdate();
        if (Current is not null) RevealInTree(Current.Document);
        _tree.Nodes.Clear();
        _scriptNodes.Clear();

        var catalog = _ws.BuildCatalog();
        foreach (var group in catalog.Groups)
        {
            var groupNode = MakeNode($"G:{group.Name}", group.Name, new CatalogTag(null, null, null, null), bold: true, expandByDefault: true);
            foreach (var serverFolder in group.Servers)
            {
                var server = serverFolder.Server;
                var serverNode = MakeNode($"S:{server.Name}", server.Name, new CatalogTag(server, null, null, null), bold: true, expandByDefault: true);
                foreach (var db in serverFolder.Databases)
                {
                    var dbNode = MakeNode($"D:{server.Name}/{db.Database}", db.Database, new CatalogTag(server, db.Database, null, null), bold: false, expandByDefault: false);
                    foreach (var type in db.Types)
                    {
                        var typeNode = MakeNode($"T:{server.Name}/{db.Database}/{type.Type}", $"{type.Type} ({type.Scripts.Count})", new CatalogTag(server, db.Database, type.Type, null), bold: false, expandByDefault: false);
                        AddScripts(typeNode, server, db.Database, type);
                        dbNode.Nodes.Add(typeNode);
                    }
                    serverNode.Nodes.Add(dbNode);
                }
                groupNode.Nodes.Add(serverNode);
            }
            _tree.Nodes.Add(groupNode);
        }

        if (catalog.Unassigned.Count > 0)
        {
            var root = MakeNode("G:_unassigned", "Unassigned (no server yet)", new CatalogTag(null, null, null, null), bold: true, expandByDefault: false);
            foreach (var type in catalog.Unassigned)
            {
                var typeNode = MakeNode($"T:_unassigned/{type.Type}", $"{type.Type} ({type.Scripts.Count})", new CatalogTag(null, null, type.Type, null), bold: false, expandByDefault: false);
                AddScripts(typeNode, null, null, type);
                root.Nodes.Add(typeNode);
            }
            _tree.Nodes.Add(root);
        }

        if (_tree.Nodes.Count == 0) _tree.Nodes.Add(new TreeNode("No servers: File > Edit Config") { ForeColor = SystemColors.GrayText });

        ApplyExpansion(_tree.Nodes);
        _initialExpandDone = true;
        _tree.EndUpdate();
        _syncing = false;
        SyncPickers();
    }

    private void OnNodeSelected(TreeNode? node)
    {
        if (_syncing || node?.Tag is not NodeInfo info) return;

        if (info.Tag.ScriptPath is not null)
        {
            OpenFromTree(info.Tag);
        }
        else
        {
            _context = info.Tag; // server / database / type node: new scripts go here
            var where = info.Tag.Server is null ? "Unassigned" : $"{info.Tag.Server.Name} / {info.Tag.Database ?? info.Tag.Server.DefaultDatabase} / {info.Tag.Type ?? _ws.Types.Default}";
            _status.Text = $"New scripts will be created in: {where}";
        }
    }

    private void OpenFromTree(CatalogTag tag)
    {
        QueryDocument? document;
        try { document = _ws.OpenScript(tag); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not open the script: {ex.Message}";
            return;
        }
        if (document is null) return;

        var page = Pages.FirstOrDefault(p => ReferenceEquals(p.Document, document));
        if (page is null)
        {
            page = AddPage(document);
            SafeSave(_ws.SaveSession);
        }
        _tabs.SelectedTab = page;
    }

    // ---------- pickers: server, database, type ----------

    private void BuildTypeButtons()
    {
        _typeBar.Items.Clear();
        _typeButtons.Clear();
        _typeBar.Items.Add(new ToolStripLabel("Type:"));
        foreach (var name in _ws.Types.Names)
        {
            var type = name;
            var button = new ToolStripButton(name) { DisplayStyle = ToolStripItemDisplayStyle.Text };
            button.Click += (_, _) => Retarget(d => _ws.SetType(d, type));
            _typeBar.Items.Add(button);
            _typeButtons[name] = button;
        }
    }

    /// <summary>Makes the toolbar and tree selection match the current script.</summary>
    private void SyncPickers()
    {
        _syncing = true;
        var document = Current?.Document;
        var server = _ws.ServerOf(document);

        _serverCombo.Items.Clear();
        _serverCombo.Items.AddRange(_ws.Config.AllServers().Cast<object>().ToArray());
        _serverCombo.SelectedItem = server;
        _databaseCombo.Items.Clear();
        if (server is not null) _databaseCombo.Items.AddRange(server.Databases.Cast<object>().ToArray());
        _databaseCombo.SelectedItem = server?.Databases.FirstOrDefault(d => d.Equals(document?.Database, StringComparison.OrdinalIgnoreCase));
        foreach (var (name, button) in _typeButtons)
            button.Checked = document is not null && name.Equals(document.Type, StringComparison.OrdinalIgnoreCase);

        _tree.SelectedNode = document?.FilePath is { } path && _scriptNodes.TryGetValue(path, out var node) ? node : null;
        _syncing = false;
        UpdateButtons();
    }

    /// <summary>Applies a server/database/type change to the current script: moves its file, then refreshes everything that shows the path.</summary>
    private void Retarget(Action<QueryDocument> change)
    {
        if (_syncing || Current is not { } page) return;
        page.SyncText();
        SafeSave(() => change(page.Document));
        page.RefreshTitle();
        SafeSave(_ws.SaveSession);
        BuildTree();
    }

    private void OnServerPicked()
    {
        if (_syncing || _serverCombo.SelectedItem is not ServerEntry server) return;
        Retarget(d => _ws.SelectServer(d, server));
    }

    private void OnDatabasePicked()
    {
        if (_syncing || _databaseCombo.SelectedItem is not string database) return;
        Retarget(d => _ws.SetDatabase(d, database));
    }

    private void UpdateButtons()
    {
        var running = Current?.IsRunning == true;
        _executeButton.Enabled = !running;
        _cancelButton.Enabled = running;
    }

    // ---------- commands ----------

    private async Task ExecuteAsync()
    {
        var page = Current;
        if (page is null || page.IsRunning) return;

        var document = page.Document;
        if (!_ws.Config.TryResolve(document.Server, document.Database, out var profile, out var error))
        {
            _status.Text = _ws.Config.ServerGroups.Count == 0
                ? "No servers yet: File > Edit Config, add a credential and a server group, then File > Reload Config."
                : error;
            return;
        }

        // Make sure what we are about to run is already safely on disk.
        page.SyncText();
        SafeSave(() => _ws.Save(document));
        _status.Text = $"Running on {profile.Name} / {profile.Database}...";
        await page.RunAsync(_ws);
        _status.Text = $"Finished on {profile.Name} / {profile.Database}";
    }

    private void ReloadConfig()
    {
        try
        {
            _ws.ReloadConfig();
            BuildTypeButtons();
            BuildTree();
            _status.Text = _ws.ConfigWarnings() ??
                $"Config reloaded: {_ws.Config.AllServers().Count()} server(s) in {_ws.Config.ServerGroups.Count} group(s), {_ws.Types.Names.Count} script type(s). Scripts folder changes need a restart.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Config error: {ex.Message}";
        }
    }

    private static void Open(string program, string path) =>
        Process.Start(new ProcessStartInfo(program, $"\"{path}\"") { UseShellExecute = true });

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        foreach (var page in Pages) page.Cancel();
        try { FlushAll(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            e.Cancel = MessageBox.Show($"Could not save your queries:\n\n{ex.Message}\n\nClose anyway?", "Desktop SQL Studio",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No;
        }
    }
}
