using System.Diagnostics;
using System.IO;
using DesktopSqlStudio.Core;

namespace DesktopSqlStudio.WinForms;

/// <summary>WinForms front end. All logic lives in <see cref="QueryWorkspace"/>; this class only draws it.</summary>
public sealed class MainForm : Form
{
    private readonly QueryWorkspace _ws;
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ToolStripLabel _serverLabel = new("(none)") { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    private readonly ToolStripComboBox _databaseCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly ToolStripButton _executeButton = new("▶ Execute (F5)");
    private readonly ToolStripButton _cancelButton = new("■ Cancel") { Enabled = false };
    private readonly ToolStripStatusLabel _status = new();
    private readonly Dictionary<string, TreeNode> _serverNodes = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncing;

    public MainForm(QueryWorkspace workspace)
    {
        _ws = workspace;
        Text = "Desktop SQL Studio";
        Width = 1200;
        Height = 800;
        KeyPreview = true;

        BuildLayout();
        BuildTree();
        foreach (var document in _ws.Documents.ToList()) AddPage(document);
        if (_tabs.TabPages.Count == 0) NewQuery();
        SyncPickers();

        _status.Text = _ws.ConfigWarnings() ?? (_ws.RestoredCount > 0
            ? $"Restored {_ws.RestoredCount} query window(s) from {_ws.Store.Folder}"
            : $"Autosaving to {_ws.Store.Folder}");
    }

    private QueryPage? Current => _tabs.SelectedTab as QueryPage;

    private IEnumerable<QueryPage> Pages => _tabs.TabPages.OfType<QueryPage>();

    // ---------- layout ----------

    private void BuildLayout()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 230 };
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_tabs);

        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(Item("&New Query", Keys.Control | Keys.N, (_, _) => NewQuery()));
        file.DropDownItems.Add(Item("&Close Query", Keys.Control | Keys.W, (_, _) => CloseCurrent()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Open &Autosave Folder", Keys.None, (_, _) => Open("explorer.exe", _ws.Store.Folder)));
        file.DropDownItems.Add(Item("&Edit Config (config.json)", Keys.None, (_, _) => Open("notepad.exe", _ws.ConfigPath)));
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
        toolbar.Items.Add(_serverLabel);
        toolbar.Items.Add(new ToolStripSeparator());
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
        Controls.Add(toolbar);
        Controls.Add(menu);
        MainMenuStrip = menu;

        _tree.AfterSelect += (_, e) => OnServerSelected(e.Node);
        _tabs.SelectedIndexChanged += (_, _) => { SyncPickers(); Current?.FocusEditor(); };
        _databaseCombo.SelectedIndexChanged += (_, _) => OnDatabaseSelected();
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
        var document = _ws.NewDocument(Current?.Document);
        var page = AddPage(document);
        SafeSave(() => { _ws.Save(document); _ws.SaveSession(); }); // the file exists from the first moment
        _tabs.SelectedTab = page;
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
    }

    // ---------- autosave ----------

    /// <summary>Writes every open query and the session list to disk right now.</summary>
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
            _status.Text = $"AUTOSAVE FAILED: {ex.Message}";
        }
    }

    // ---------- servers and databases ----------

    private void BuildTree()
    {
        _syncing = true;
        _tree.Nodes.Clear();
        _serverNodes.Clear();
        foreach (var group in _ws.Config.ServerGroups)
        {
            var groupNode = new TreeNode(group.Name) { NodeFont = new Font(Font, FontStyle.Bold) };
            foreach (var server in group.Servers)
            {
                var node = new TreeNode(server.Name) { Tag = server };
                groupNode.Nodes.Add(node);
                _serverNodes.TryAdd(server.Name, node);
            }
            _tree.Nodes.Add(groupNode);
            groupNode.Expand();
        }
        if (_tree.Nodes.Count == 0) _tree.Nodes.Add(new TreeNode("No servers: File > Edit Config") { ForeColor = SystemColors.GrayText });
        _syncing = false;
        SyncPickers();
    }

    /// <summary>Makes the tree selection, server label and database dropdown match the current query window.</summary>
    private void SyncPickers()
    {
        _syncing = true;
        var document = Current?.Document;
        var server = _ws.ServerOf(document);

        _tree.SelectedNode = server is not null && _serverNodes.TryGetValue(server.Name, out var node) ? node : null;
        _serverLabel.Text = server?.Name ?? "(none)";

        _databaseCombo.Items.Clear();
        if (server is not null) _databaseCombo.Items.AddRange(server.Databases.Cast<object>().ToArray());
        _databaseCombo.SelectedItem = server?.Databases.FirstOrDefault(d => d.Equals(document?.Database, StringComparison.OrdinalIgnoreCase));
        _syncing = false;
        UpdateButtons();
    }

    private void OnServerSelected(TreeNode? node)
    {
        if (_syncing || Current is null || node?.Tag is not ServerEntry server) return; // a group row: ignore
        _ws.SelectServer(Current.Document, server);
        SyncPickers();
        SafeSave(_ws.SaveSession);
    }

    private void OnDatabaseSelected()
    {
        if (_syncing || Current is null || _databaseCombo.SelectedItem is not string database) return;
        Current.Document.Database = database;
        SafeSave(_ws.SaveSession);
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
            BuildTree();
            _status.Text = _ws.ConfigWarnings() ??
                $"Config reloaded: {_ws.Config.AllServers().Count()} server(s) in {_ws.Config.ServerGroups.Count} group(s). Autosave folder changes need a restart.";
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
