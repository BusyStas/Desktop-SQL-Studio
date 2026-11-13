using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DesktopSqlStudio.Core;
using ICSharpCode.AvalonEdit.Highlighting;

namespace DesktopSqlStudio;

/// <summary>One query window: a T-SQL editor on top, result grids and messages below.</summary>
public sealed partial class QueryView : UserControl
{
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _cts;

    public string Id { get; }
    public string Title { get; set; }
    public string? ServerName { get; set; }
    public string? Database { get; set; }
    public bool IsRunning { get; private set; }
    public string Text => Editor.Text;

    /// <summary>Raised 500 ms after the user stops typing: the host saves the text to disk.</summary>
    public event EventHandler? Changed;
    public event EventHandler? RunningChanged;

    public QueryView(SavedQuery saved)
    {
        InitializeComponent();
        Id = saved.Id;
        Title = saved.Title;
        ServerName = saved.Server;
        Database = saved.Database;

        Editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(".sql");
        Editor.Text = saved.Text;

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        Editor.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
    }

    public void FocusEditor() => Editor.Focus();

    public void Cancel() => _cts?.Cancel();

    /// <summary>Runs the selected text, or the whole script when nothing is selected.</summary>
    public async Task RunAsync(ConnectionProfile profile)
    {
        if (IsRunning) return;
        var sql = Editor.SelectionLength > 0 ? Editor.SelectedText : Editor.Text;
        if (string.IsNullOrWhiteSpace(sql)) return;

        _cts = new CancellationTokenSource();
        SetRunning(true);
        ShowMessagesOnly("Executing...");

        QueryResult result;
        try
        {
            result = await QueryExecutor.ExecuteAsync(profile, sql, _cts.Token);
        }
        catch (Exception ex)
        {
            result = new QueryResult { Failed = true };
            result.Messages.Add(ex.Message);
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
        ResultsTabs.Items.Clear();
        ResultsTabs.Items.Add(MessagesTab(text));
        ResultsTabs.SelectedIndex = 0;
    }

    private void Render(QueryResult result)
    {
        ResultsTabs.Items.Clear();
        for (var i = 0; i < result.Tables.Count; i++)
        {
            var table = result.Tables[i];
            ResultsTabs.Items.Add(new TabItem { Header = $"Results {i + 1} ({table.Rows.Count:N0} rows)", Content = BuildGrid(table) });
        }

        var messages = string.Join(Environment.NewLine, result.Messages);
        var summary = result.Failed ? "Completed with errors" : "Completed successfully";
        ResultsTabs.Items.Add(MessagesTab($"{messages}{(messages.Length > 0 ? Environment.NewLine + Environment.NewLine : "")}{summary} in {result.Elapsed.TotalSeconds:0.000} s"));

        ResultsTabs.SelectedIndex = result.Failed || result.Tables.Count == 0 ? ResultsTabs.Items.Count - 1 : 0;
    }

    private static TabItem MessagesTab(string text) => new()
    {
        Header = "Messages",
        Content = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        }
    };

    private static DataGrid BuildGrid(DataTable table)
    {
        var grid = new DataGrid
        {
            IsReadOnly = true,
            AutoGenerateColumns = true,
            CanUserAddRows = false,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            EnableRowVirtualization = true,
            ItemsSource = table.DefaultView
        };
        grid.AutoGeneratingColumn += (_, e) =>
        {
            // Column names can contain dots, brackets or underscores, so bind by indexer
            // and escape underscores that WPF would otherwise treat as access keys.
            var name = e.PropertyName;
            e.Column = new DataGridTextColumn
            {
                Header = name.Replace("_", "__"),
                Binding = new Binding($"[{name}]") { Converter = CellDisplayConverter.Instance, Mode = BindingMode.OneWay }
            };
        };
        return grid;
    }

    // Stop the results tab selection from bubbling up and being mistaken for a query-tab change.
    private void ResultsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => e.Handled = true;
}
