using System.Drawing;
using System.Windows.Forms;
using MediaCleanup.Core.Activity;
using MediaCleanup.Core.Configuration;

namespace MediaCleanup.ConfigApp;

/// <summary>
/// The Recent Activity tab: one row per processed download, and the steps done to the selected
/// download underneath. It reads the service's log files, so it also shows what happened while
/// this app was closed.
/// </summary>
internal sealed class ActivityPanel : UserControl
{
    private const int MaxItems = 300;
    private static readonly Color ProblemColor = Color.FromArgb(198, 40, 40);
    private static readonly Color DoneColor = Color.FromArgb(46, 125, 50);

    private readonly ListView _items = NewList();
    private readonly ListView _steps = NewList();
    private readonly Label _stepsTitle = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 4), Text = "Select a file above to see what was done to it." };
    private readonly Label _problem = new() { AutoSize = true, ForeColor = ProblemColor, Margin = new Padding(3, 3, 3, 6), Visible = false };
    private readonly CheckBox _problemsOnly = new() { Text = "Only show files with problems", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
    private List<ItemActivity> _loaded = new();
    private string _signature = "";

    public ActivityPanel()
    {
        Dock = DockStyle.Fill;

        _items.Columns.Add("Time", 130);
        _items.Columns.Add("File or folder", 380);
        _items.Columns.Add("Result", 90);
        _items.Columns.Add("What was done", 400);
        _steps.Columns.Add("Time", 80);
        _steps.Columns.Add("Step", 90);
        _steps.Columns.Add("Details", 830);
        _items.SelectedIndexChanged += (_, _) => ShowSteps();

        var refresh = new Button { Text = "Refresh", AutoSize = true, MinimumSize = new Size(90, 28) };
        refresh.Click += (_, _) => Reload(force: true);
        _problemsOnly.CheckedChanged += (_, _) => Reload(force: true);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        toolbar.Controls.Add(refresh);
        toolbar.Controls.Add(_problemsOnly);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_problem, 0, 1);
        layout.Controls.Add(_items, 0, 2);
        layout.Controls.Add(_stepsTitle, 0, 3);
        layout.Controls.Add(_steps, 0, 4);
        Controls.Add(layout);
    }

    private static ListView NewList() => new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        GridLines = true,
        ShowItemToolTips = true,
    };

    /// <summary>Reads the log again. The lists are only rebuilt when something changed.</summary>
    public void Reload(bool force = false)
    {
        List<ItemActivity> items;
        ActivityEntry? latestProblem;
        try
        {
            (items, latestProblem) = ActivityLog.ReadRecentItems(AppPaths.LogDirectory, MaxItems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (_problemsOnly.Checked)
            items = items.Where(i => i.Result == ItemResult.Failed || i.Steps.Any(s => s.IsProblem)).ToList();

        var signature = string.Join("|", items.Select(i => $"{i.Started.Ticks}:{i.Name}:{i.Result}:{i.Steps.Count}"))
                        + "#" + latestProblem?.Time.Ticks + latestProblem?.Message;
        if (!force && signature == _signature) return;
        _signature = signature;

        // Only a problem from the last day is worth a banner; older ones have usually been dealt with.
        var showProblem = latestProblem != null && latestProblem.Time > DateTime.Now.AddDays(-1);
        _problem.Visible = showProblem;
        if (showProblem)
            _problem.Text = $"Latest service problem ({latestProblem!.Time:g}): {FirstLine(latestProblem.Message)}";

        var selectedKey = _items.SelectedItems.Count > 0 && _items.SelectedItems[0].Tag is ItemActivity selected ? Key(selected) : null;
        _loaded = items;

        _items.BeginUpdate();
        _items.Items.Clear();
        foreach (var item in items)
        {
            var row = new ListViewItem(item.Started.ToString("yyyy-MM-dd HH:mm:ss")) { Tag = item };
            row.SubItems.Add(item.Name);
            row.SubItems.Add(Describe(item.Result));
            row.SubItems.Add(item.Summary);
            row.ToolTipText = item.SourcePath;
            if (item.Result == ItemResult.Failed) row.ForeColor = ProblemColor;
            _items.Items.Add(row);
            if (selectedKey != null && Key(item) == selectedKey) row.Selected = true;
        }
        if (items.Count == 0)
        {
            var empty = new ListViewItem("") { ForeColor = SystemColors.GrayText };
            empty.SubItems.Add(_problemsOnly.Checked ? "No files with problems." : "Nothing has been processed yet.");
            _items.Items.Add(empty);
        }
        _items.EndUpdate();
        ShowSteps();
    }

    private void ShowSteps()
    {
        _steps.BeginUpdate();
        _steps.Items.Clear();
        if (_items.SelectedItems.Count > 0 && _items.SelectedItems[0].Tag is ItemActivity item)
        {
            _stepsTitle.Text = item.Result == ItemResult.Done && item.FinalPath != null
                ? $"What was done to \"{item.Name}\"  -  now at: {item.FinalPath}"
                : $"What was done to \"{item.Name}\"  -  found at: {item.SourcePath}";

            foreach (var step in item.Steps)
                AddStep(step.Time, Describe(step.Kind), step.Message.Replace("\n", "  "), step.IsProblem ? ProblemColor : (Color?)null);

            if (item.Steps.Count == 0 && item.Result == ItemResult.Done)
                AddStep(item.Started, "Checked", "Nothing needed changing.", null);
            switch (item.Result)
            {
                case ItemResult.Done:
                    AddStep(item.Steps.Count > 0 ? item.Steps[^1].Time : item.Started, "Finished", item.FinalPath ?? "", DoneColor);
                    break;
                case ItemResult.InProgress:
                    AddStep(DateTime.Now, "Working", "The service is still working on this.", null);
                    break;
                case ItemResult.Interrupted:
                    AddStep(item.Started, "Interrupted", "The service stopped before it finished. It starts again on the next scan.", ProblemColor);
                    break;
            }
        }
        else
        {
            _stepsTitle.Text = "Select a file above to see what was done to it.";
        }
        _steps.EndUpdate();
    }

    private void AddStep(DateTime time, string step, string details, Color? color)
    {
        var row = new ListViewItem(time.ToString("HH:mm:ss"));
        row.SubItems.Add(step);
        row.SubItems.Add(details);
        row.ToolTipText = details;
        if (color != null) row.ForeColor = color.Value;
        _steps.Items.Add(row);
    }

    private static string Key(ItemActivity item) => item.Started.Ticks + "|" + item.SourcePath;

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        return index >= 0 ? text[..index] : text;
    }

    private static string Describe(ItemResult result) => result switch
    {
        ItemResult.Done => "Done",
        ItemResult.Failed => "Failed",
        ItemResult.Interrupted => "Interrupted",
        _ => "Working...",
    };

    private static string Describe(ActivityKind kind) => kind switch
    {
        ActivityKind.Rename => "Renamed",
        ActivityKind.Tracks => "MKV tracks",
        ActivityKind.Subtitle => "Subtitle",
        ActivityKind.Delete => "Removed",
        ActivityKind.Move => "Moved",
        ActivityKind.Warning => "Warning",
        ActivityKind.Error => "Error",
        _ => "Info",
    };
}
