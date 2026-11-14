using System.Drawing;
using System.Windows.Forms;
using MediaCleanup.Core.Configuration;
using MediaCleanup.Core.Mkv;
using MediaCleanup.Core.Processing;
using Microsoft.Extensions.Logging;

namespace MediaCleanup.ConfigApp;

/// <summary>
/// The Manual Cleanup tab: runs chosen actions once over an existing folder, in place. The run
/// happens inside this app (not the service), under the account of the person using it.
/// </summary>
internal sealed class ManualPanel : UserControl
{
    private static readonly Color ProblemColor = Color.FromArgb(198, 40, 40);

    private readonly Func<AppConfig> _getConfig;
    private readonly TextBox _folder = new() { Dock = DockStyle.Fill };
    private readonly RadioButton _movies = new() { Text = "Movies", AutoSize = true, Checked = true };
    private readonly RadioButton _tvShows = new() { Text = "TV shows", AutoSize = true, Margin = new Padding(12, 3, 3, 3) };
    // Nothing is ticked to begin with: the person chooses each action deliberately.
    private readonly CheckBox _nameCleanup = new() { Text = "Name cleanup: remove website names from file and folder names, dots become spaces", AutoSize = true };
    private readonly CheckBox _trackCleanup = new() { Text = "Track names cleanup: remove website names from MKV title and track names, remove tags", AutoSize = true };
    private readonly CheckBox _defaultAudio = new() { Text = "Set default audio track: Telugu 5.1 > English 5.1 > Telugu > English > Hindi 5.1 > Hindi", AutoSize = true };
    private readonly CheckBox _subtitleCleanup = new() { Text = "Subtitle cleanup: clean subtitle file names and remove website lines inside subtitles", AutoSize = true };
    private readonly CheckBox _moveIntoFolder = new() { Text = "Move under folder: give a loose movie file its own folder named after the cleaned file name", AutoSize = true };
    private readonly CheckBox _removeUnwanted = new() { Text = "Remove unwanted files (extensions are listed on the Settings tab)", AutoSize = true };
    private readonly Button _preview = new() { Text = "Preview", AutoSize = true, MinimumSize = new Size(110, 30) };
    private readonly Button _run = new() { Text = "Run cleanup", AutoSize = true, MinimumSize = new Size(110, 30) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 30), Enabled = false };
    private readonly Label _state = new() { AutoSize = true, Margin = new Padding(12, 9, 3, 3) };
    private readonly TextBox _output = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        BackColor = SystemColors.Window,
        Font = new Font("Consolas", 9F),
    };
    private CancellationTokenSource? _cancellation;

    public ManualPanel(Func<AppConfig> getConfig)
    {
        _getConfig = getConfig;
        Dock = DockStyle.Fill;

        var hint = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 3, 3, 8),
            Text = "Clean up a folder you already have, such as your existing library. Every sub-folder and every loose media file in it is cleaned in place;\n" +
                   "nothing is moved to another location. Tick the actions you want, use Preview to see what would happen, then Run cleanup.",
        };

        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
        };
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.Controls.Add(new Label { Text = "Folder to clean:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 6) }, 0, 0);
        folderRow.Controls.Add(_folder, 1, 0);
        folderRow.Controls.Add(browse, 2, 0);

        var kindRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        kindRow.Controls.Add(new Label { Text = "The folder contains:", AutoSize = true, Margin = new Padding(3, 6, 8, 3) });
        kindRow.Controls.Add(_movies);
        kindRow.Controls.Add(_tvShows);
        _movies.CheckedChanged += (_, _) =>
        {
            _moveIntoFolder.Enabled = _movies.Checked;
            if (!_movies.Checked) _moveIntoFolder.Checked = false;
        };

        var actions = new GroupBox { Text = "Actions (none are selected until you tick them)", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 6, 10, 6) };
        var actionList = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        actionList.Controls.AddRange(new Control[] { _nameCleanup, _trackCleanup, _defaultAudio, _subtitleCleanup, _moveIntoFolder, _removeUnwanted });
        actions.Controls.Add(actionList);

        _preview.Click += async (_, _) => await StartAsync(preview: true);
        _run.Click += async (_, _) => await StartAsync(preview: false);
        _cancel.Click += (_, _) =>
        {
            _cancellation?.Cancel();
            _state.Text = "Stopping after the current file...";
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        buttons.Controls.AddRange(new Control[] { _preview, _run, _cancel, _state });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(folderRow, 0, 1);
        layout.Controls.Add(kindRow, 0, 2);
        layout.Controls.Add(actions, 0, 3);
        layout.Controls.Add(buttons, 0, 4);
        layout.Controls.Add(_output, 0, 5);
        Controls.Add(layout);
    }

    /// <summary>True while a run is in progress, so the window can warn before closing.</summary>
    public bool IsRunning => _cancellation != null;

    private async Task StartAsync(bool preview)
    {
        if (IsRunning) return;

        var folder = PathUtil.NormalizeDir(_folder.Text);
        var actions = ManualCleanupOptions.NoActions();
        actions.NameCleanup = _nameCleanup.Checked;
        actions.TrackNameCleanup = _trackCleanup.Checked;
        actions.SetDefaultAudio = _defaultAudio.Checked;
        actions.SubtitleCleanup = _subtitleCleanup.Checked;
        actions.MoveIntoFolder = _movies.Checked && _moveIntoFolder.Checked;
        actions.RemoveUnwantedFiles = _removeUnwanted.Checked;
        var options = new ManualCleanupOptions
        {
            Folder = folder,
            Kind = _movies.Checked ? MediaKind.Movie : MediaKind.TvShow,
            Actions = actions,
            Preview = preview,
        };

        var config = _getConfig();
        var problem = Validate(options, config);
        if (problem != null)
        {
            MessageBox.Show(this, problem, "Manual cleanup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!preview)
        {
            var answer = MessageBox.Show(this,
                $"This changes files in:\n{folder}\n\nRenames, removed files and MKV edits cannot be undone automatically. " +
                "Run Preview first if you have not seen what will happen.\n\nRun the cleanup now?",
                "Manual cleanup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }

        _cancellation = new CancellationTokenSource();
        SetBusy(true);
        _output.Clear();
        _state.ForeColor = SystemColors.ControlText;
        _state.Text = preview ? "Previewing..." : "Cleaning...";
        var prefix = preview ? "[preview] " : "";
        var logger = new CallbackLogger((level, message) => Append(prefix + Label(level) + message));
        var token = _cancellation.Token;

        try
        {
            var tools = new MkvToolRunner(config.MkvToolNixPath);
            var result = await Task.Run(() => ManualCleanup.RunAsync(config, options, tools, logger, token));
            var summary = $"{result.Processed} item(s) {(preview ? "checked" : "cleaned")}, {result.Skipped} skipped, {result.Failed} failed";
            _state.ForeColor = result.Failed > 0 ? ProblemColor : SystemColors.ControlText;
            _state.Text = (result.Cancelled ? "Cancelled: " : preview ? "Preview finished, nothing was changed: " : "Finished: ") + summary + ".";
            Append("");
            Append(_state.Text);
        }
        catch (Exception ex)
        {
            _state.ForeColor = ProblemColor;
            _state.Text = "Stopped: " + ex.Message;
            Append("ERROR  " + ex.Message);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false);
        }
    }

    private static string? Validate(ManualCleanupOptions options, AppConfig config)
    {
        if (options.Folder.Length == 0) return "Please choose the folder to clean.";
        if (!Path.IsPathFullyQualified(options.Folder)) return "The folder must be a full path such as D:\\Movies or \\\\server\\share\\Movies.";
        if (!Directory.Exists(options.Folder)) return "The folder was not found or cannot be opened from this account:\n" + options.Folder;
        if (!options.HasAnyAction) return "No actions are selected. Tick at least one action to run.";

        // The service must not work on the same files at the same time.
        var monitored = config.Mappings.FirstOrDefault(m => m.Active && config.MonitoringEnabled
            && (PathUtil.IsSameOrInside(options.Folder, m.SourcePath) || PathUtil.IsSameOrInside(m.SourcePath, options.Folder)));
        if (monitored != null)
            return $"This folder overlaps the monitored source \"{monitored.SourcePath}\".\n\n" +
                   "Untick that source (or switch monitoring to INACTIVE) before cleaning it by hand, so the service does not touch the same files at the same time.";
        return null;
    }

    private void SetBusy(bool busy)
    {
        _preview.Enabled = !busy;
        _run.Enabled = !busy;
        _cancel.Enabled = busy;
    }

    private void Append(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action<string>(Append), line);
            }
            catch (InvalidOperationException)
            {
                // The window closed while the run was finishing.
            }
            return;
        }
        _output.AppendText(line + Environment.NewLine);
    }

    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Warning => "WARN   ",
        LogLevel.Error or LogLevel.Critical => "ERROR  ",
        _ => "",
    };

    private sealed class CallbackLogger : ILogger
    {
        private readonly Action<LogLevel, string> _write;

        public CallbackLogger(Action<LogLevel, string> write) => _write = write;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) _write(logLevel, formatter(state, exception));
        }
    }
}
