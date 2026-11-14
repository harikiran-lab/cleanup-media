using System.Drawing;
using System.Windows.Forms;
using MediaCleanup.Core.Configuration;

namespace MediaCleanup.ConfigApp;

/// <summary>Adds or edits one monitored source and its destination.</summary>
internal sealed class MappingDialog : Form
{
    private readonly MonitorMapping _mapping;
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly TextBox _source = new() { Dock = DockStyle.Fill };
    private readonly TextBox _destination = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _active = new() { Text = "Active (monitor this source)", AutoSize = true };
    private readonly CheckBox _nameCleanup = new() { Text = "Name cleanup: remove website names from file and folder names, dots become spaces", AutoSize = true };
    private readonly CheckBox _trackCleanup = new() { Text = "Track names cleanup: remove website names from MKV title and track names, remove tags (MKVToolNix)", AutoSize = true };
    private readonly CheckBox _defaultAudio = new() { Text = "Set default audio track: Telugu 5.1 > English 5.1 > Telugu > English > Hindi 5.1 > Hindi (MKVToolNix)", AutoSize = true };
    private readonly CheckBox _subtitleCleanup = new() { Text = "Subtitle cleanup: clean subtitle file names and remove website lines inside subtitles", AutoSize = true };
    private readonly CheckBox _moveIntoFolder = new() { Text = "Move under folder: give a loose movie file its own folder named after the cleaned file name", AutoSize = true };
    private readonly CheckBox _removeUnwanted = new() { Text = "Remove unwanted files (extensions are listed on the Settings tab)", AutoSize = true };

    /// <param name="mapping">A copy of the entry to edit. It is only changed when the user confirms.</param>
    public MappingDialog(MonitorMapping mapping, bool isNew)
    {
        _mapping = mapping;
        var kindText = mapping.Kind == MediaKind.Movie ? "movie" : "TV show";
        Text = (isNew ? "Add " : "Edit ") + kindText + " source";
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 470);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12), AutoSize = false };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        AddRow(grid, "Name:", _name, null);
        AddRow(grid, "Source folder:", _source, BrowseButton(_source));
        AddRow(grid, "Destination folder:", _destination, BrowseButton(_destination));

        var hint = new Label
        {
            Text = "Local or network (UNC) paths, for example \\\\server\\share\\Downloads. You can type or paste a path.\n" +
                   "Leave the destination empty to clean up in place without moving anything elsewhere.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 10),
        };
        AddFullRow(grid, hint, 1);
        AddFullRow(grid, _active, 1);

        var actions = new GroupBox { Text = "Actions", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(3, 10, 3, 3) };
        var actionList = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        actionList.Controls.AddRange(new Control[] { _nameCleanup, _trackCleanup, _defaultAudio, _subtitleCleanup });
        if (mapping.Kind == MediaKind.Movie) actionList.Controls.Add(_moveIntoFolder);
        actionList.Controls.Add(_removeUnwanted);
        actions.Controls.Add(actionList);
        AddFullRow(grid, actions, 0);

        var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 28), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Confirm();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(grid);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        _name.Text = mapping.Name;
        _source.Text = mapping.SourcePath;
        _destination.Text = mapping.DestinationPath;
        _active.Checked = mapping.Active;
        _nameCleanup.Checked = mapping.Actions.NameCleanup;
        _trackCleanup.Checked = mapping.Actions.TrackNameCleanup;
        _defaultAudio.Checked = mapping.Actions.SetDefaultAudio;
        _subtitleCleanup.Checked = mapping.Actions.SubtitleCleanup;
        _moveIntoFolder.Checked = mapping.Actions.MoveIntoFolder;
        _removeUnwanted.Checked = mapping.Actions.RemoveUnwantedFiles;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control editor, Control? extra)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 6) }, 0, row);
        grid.Controls.Add(editor, 1, row);
        if (extra != null) grid.Controls.Add(extra, 2, row);
        else grid.SetColumnSpan(editor, 2);
    }

    private static void AddFullRow(TableLayoutPanel grid, Control control, int firstColumn)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(control, firstColumn, row);
        grid.SetColumnSpan(control, 3 - firstColumn);
    }

    private Button BrowseButton(TextBox target)
    {
        var button = new Button { Text = "Browse...", AutoSize = true };
        button.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { ShowNewFolderButton = true };
            var current = target.Text.Trim();
            try
            {
                if (current.Length > 0 && Directory.Exists(current)) dialog.SelectedPath = current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreachable share only means the dialog opens at its default location.
            }
            if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.SelectedPath;
        };
        return button;
    }

    private void Confirm()
    {
        var source = PathUtil.NormalizeDir(_source.Text);
        var destination = PathUtil.NormalizeDir(_destination.Text);

        if (source.Length == 0)
        {
            Warn("Please enter the source folder to monitor.", _source);
            return;
        }
        if (!Path.IsPathFullyQualified(source))
        {
            Warn("The source must be a full path such as D:\\Downloads or \\\\server\\share\\Downloads.", _source);
            return;
        }
        if (destination.Length > 0 && !Path.IsPathFullyQualified(destination))
        {
            Warn("The destination must be a full path such as D:\\Movies or \\\\server\\share\\Movies.", _destination);
            return;
        }

        _mapping.Name = _name.Text.Trim();
        _mapping.SourcePath = source;
        _mapping.DestinationPath = destination;
        _mapping.Active = _active.Checked;
        _mapping.Actions.NameCleanup = _nameCleanup.Checked;
        _mapping.Actions.TrackNameCleanup = _trackCleanup.Checked;
        _mapping.Actions.SetDefaultAudio = _defaultAudio.Checked;
        _mapping.Actions.SubtitleCleanup = _subtitleCleanup.Checked;
        _mapping.Actions.MoveIntoFolder = _mapping.Kind == MediaKind.Movie && _moveIntoFolder.Checked;
        _mapping.Actions.RemoveUnwantedFiles = _removeUnwanted.Checked;

        DialogResult = DialogResult.OK;
        Close();
    }

    private void Warn(string message, Control focus)
    {
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        focus.Focus();
    }
}
