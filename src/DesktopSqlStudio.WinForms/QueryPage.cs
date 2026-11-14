using System.Data;
using DesktopSqlStudio.Core;
using Timer = System.Windows.Forms.Timer;

namespace DesktopSqlStudio.WinForms;

/// <summary>One query window: an editor on top, result grids and messages below.</summary>
public sealed class QueryPage : TabPage
{
    private readonly TextBox _editor = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        AcceptsTab = true,
        AcceptsReturn = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        Font = new Font("Consolas", 10.5f),
        HideSelection = false
    };

    private readonly TabControl _results = new() { Dock = DockStyle.Fill };
    private readonly Timer _debounce = new() { Interval = 500 };
    private CancellationTokenSource? _cts;

    public QueryDocument Document { get; }
    public bool IsRunning { get; private set; }

    /// <summary>Raised 500 ms after the user stops typing: the host saves the text to disk.</summary>
    public event EventHandler? Changed;
    public event EventHandler? RunningChanged;

    public QueryPage(QueryDocument document)
    {
        Document = document;
        Text = document.Title;

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };
        split.Panel1.Controls.Add(_editor);
        split.Panel2.Controls.Add(_results);
        Controls.Add(split);

        _editor.Text = document.Text;
        _editor.SelectionStart = 0;
        _editor.SelectionLength = 0;

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            SyncText();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _editor.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
    }

    /// <summary>Copies the editor text into the document so the workspace can save it.</summary>
    public void SyncText() => Document.Text = _editor.Text;

    /// <summary>Shows the document's current file name on the tab.</summary>
    public void RefreshTitle() => Text = Document.Title;

    public void FocusEditor() => _editor.Focus();

    public void Cancel() => _cts?.Cancel();

    /// <summary>Runs the selected text, or the whole script when nothing is selected.</summary>
    public async Task RunAsync(QueryWorkspace workspace)
    {
        if (IsRunning) return;
        var sql = _editor.SelectionLength > 0 ? _editor.SelectedText : _editor.Text;
        if (string.IsNullOrWhiteSpace(sql)) return;

        _cts = new CancellationTokenSource();
        SetRunning(true);
        ShowMessagesOnly("Executing...");

        QueryResult result;
        try
        {
            result = await workspace.RunAsync(Document, sql, _cts.Token);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
        Render(result);
    }

    private void SetRunning(bool running)
    {
        IsRunning = running;
        RunningChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowMessagesOnly(string text)
    {
        _results.TabPages.Clear();
        _results.TabPages.Add(MessagesPage(text));
    }

    private void Render(QueryResult result)
    {
        _results.TabPages.Clear();
        for (var i = 0; i < result.Tables.Count; i++)
        {
            var table = result.Tables[i];
            var page = new TabPage($"Results {i + 1} ({table.Rows.Count:N0} rows)");
            page.Controls.Add(BuildGrid(table));
            _results.TabPages.Add(page);
        }

        var messages = string.Join(Environment.NewLine, result.Messages);
        var summary = result.Failed ? "Completed with errors" : "Completed successfully";
        var separator = messages.Length > 0 ? Environment.NewLine + Environment.NewLine : "";
        _results.TabPages.Add(MessagesPage($"{messages}{separator}{summary} in {result.Elapsed.TotalSeconds:0.000} s"));

        _results.SelectedIndex = result.Failed || result.Tables.Count == 0 ? _results.TabPages.Count - 1 : 0;
    }

    private static TabPage MessagesPage(string text)
    {
        var page = new TabPage("Messages");
        page.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 10f),
            Text = text
        });
        return page;
    }

    private static DataGridView BuildGrid(DataTable table)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithAutoHeaderText,
            DataSource = table
        };
        grid.DefaultCellStyle.DataSourceNullValue = DBNull.Value;
        grid.DefaultCellStyle.NullValue = "NULL";
        return grid;
    }
}
