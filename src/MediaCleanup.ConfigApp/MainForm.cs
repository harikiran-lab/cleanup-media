using System.Diagnostics;
using System.Drawing;
using System.ServiceProcess;
using System.Windows.Forms;
using MediaCleanup.Core.Configuration;
using MediaCleanup.Core.Mkv;

namespace MediaCleanup.ConfigApp;

/// <summary>
/// The configuration window. Every change is written to the shared configuration file straight
/// away, and the service picks it up on its next scan without a restart.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color ActiveColor = Color.FromArgb(46, 125, 50);
    private static readonly Color InactiveColor = Color.FromArgb(198, 40, 40);

    private AppConfig _config = new();
    private bool _loading;

    private readonly Button _monitoringButton = new() { Size = new Size(280, 52), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Margin = new Padding(3, 3, 20, 3) };
    private readonly Label _serviceLabel = new() { AutoSize = true, Margin = new Padding(3, 8, 12, 3) };
    private readonly Button _installButton = new() { AutoSize = true, MinimumSize = new Size(110, 28) };
    private readonly Button _startButton = new() { Text = "Start service", AutoSize = true, MinimumSize = new Size(100, 28) };
    private readonly Button _stopButton = new() { Text = "Stop service", AutoSize = true, MinimumSize = new Size(100, 28) };
    private readonly ListView _movies = NewList();
    private readonly ListView _tvShows = NewList();
    private readonly NumericUpDown _scanInterval = new() { Minimum = 5, Maximum = 3600, Width = 90 };
    private readonly NumericUpDown _stableSeconds = new() { Minimum = 5, Maximum = 86400, Width = 90 };
    private readonly TextBox _mkvPath = new() { Dock = DockStyle.Fill };
    private readonly Label _mkvStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly TextBox _unwanted = new() { Dock = DockStyle.Fill };
    private readonly TextBox _customTerms = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 110, AcceptsReturn = true };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly ActivityPanel _activity = new();
    private bool _activityVisible;
    private ManualPanel? _manual;

    public MainForm()
    {
        Text = "Media Cleanup";
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 560);
        Size = new Size(1080, 660);

        try
        {
            // The window and taskbar use the icon that is built into the program file.
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // Keep the default icon.
        }

        BuildLayout();
        LoadConfig();
        RefreshServiceStatus();
        _timer.Tick += (_, _) =>
        {
            RefreshServiceStatus();
            if (_activityVisible) _activity.Reload();
        };
        _timer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_manual is { IsRunning: true })
        {
            var answer = MessageBox.Show(this, "A manual cleanup is still running. Closing now stops it part-way.\n\nClose anyway?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) e.Cancel = true;
        }
        base.OnFormClosing(e);
    }

    private static ListView NewList()
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            GridLines = true,
        };
        list.Columns.Add("Active / Name", 190);
        list.Columns.Add("Source", 300);
        list.Columns.Add("Destination", 300);
        list.Columns.Add("Actions", 240);
        return list;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Header: the master switch and the service controls.
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        _monitoringButton.Font = new Font(Font.FontFamily, 12F, FontStyle.Bold);
        _monitoringButton.FlatAppearance.BorderSize = 0;
        _monitoringButton.Click += (_, _) => ToggleMonitoring();
        _installButton.Click += (_, _) => InstallOrUninstall();
        _startButton.Click += (_, _) => RunServiceAction("start the service", ServiceManager.Start);
        _stopButton.Click += (_, _) => RunServiceAction("stop the service", ServiceManager.Stop);
        var servicesButton = new Button { Text = "Windows Services...", AutoSize = true, MinimumSize = new Size(130, 28) };
        servicesButton.Click += (_, _) => OpenShell("services.msc");
        var logButton = new Button { Text = "Open log folder", AutoSize = true, MinimumSize = new Size(120, 28) };
        logButton.Click += (_, _) =>
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            OpenShell(AppPaths.LogDirectory);
        };
        var serviceControls = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        serviceControls.Controls.AddRange(new Control[] { _serviceLabel, _installButton, _startButton, _stopButton, servicesButton, logButton });
        header.Controls.Add(_monitoringButton);
        header.Controls.Add(serviceControls);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildListTab("Movies", MediaKind.Movie, _movies));
        tabs.TabPages.Add(BuildListTab("TV Shows", MediaKind.TvShow, _tvShows));
        var activityPage = new TabPage("Recent Activity") { Padding = new Padding(8) };
        activityPage.Controls.Add(_activity);
        tabs.TabPages.Add(activityPage);
        var manualPage = new TabPage("Manual Cleanup") { Padding = new Padding(8) };
        _manual = new ManualPanel(() => _config);
        manualPage.Controls.Add(_manual);
        tabs.TabPages.Add(manualPage);
        tabs.TabPages.Add(BuildSettingsTab());
        tabs.SelectedIndexChanged += (_, _) =>
        {
            _activityVisible = tabs.SelectedTab == activityPage;
            if (_activityVisible) _activity.Reload();
        };

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        root.Controls.Add(_status, 0, 2);
        Controls.Add(root);
    }

    private TabPage BuildListTab(string title, MediaKind kind, ListView list)
    {
        var page = new TabPage(title) { Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hint = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 3, 3, 8),
            Text = kind == MediaKind.Movie
                ? "Each entry watches a download folder for movies. Tick the box to make an entry active, untick it to pause it."
                : "Each entry watches a download folder for TV show folders. The show folder and its episodes are cleaned and moved to the destination.",
        };

        var add = new Button { Text = "Add...", AutoSize = true, MinimumSize = new Size(90, 28) };
        var edit = new Button { Text = "Edit...", AutoSize = true, MinimumSize = new Size(90, 28) };
        var remove = new Button { Text = "Remove", AutoSize = true, MinimumSize = new Size(90, 28) };
        add.Click += (_, _) => AddMapping(kind);
        edit.Click += (_, _) => EditSelected(list);
        remove.Click += (_, _) => RemoveSelected(list);
        list.ItemChecked += (_, e) => OnItemChecked(e.Item);
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) EditSelected(list);
            else if (e.KeyCode == Keys.Delete) RemoveSelected(list);
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange(new Control[] { add, edit, remove });

        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildSettingsTab()
    {
        var page = new TabPage("Settings") { Padding = new Padding(12) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void AddRow(string label, Control editor, Control? extra = null, string? hint = null)
        {
            var row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 6) }, 0, row);
            grid.Controls.Add(editor, 1, row);
            if (extra != null) grid.Controls.Add(extra, 2, row);
            if (hint == null) return;
            var hintRow = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 10) }, 1, hintRow);
        }

        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK) _mkvPath.Text = dialog.SelectedPath;
        };

        AddRow("Scan every (seconds):", _scanInterval, hint: "How often the service looks into each source folder.");
        AddRow("Wait until unchanged for (seconds):", _stableSeconds,
            hint: "A download counts as complete when its size and date stay the same for this long and no other program has it open.");
        AddRow("MKVToolNix folder:", _mkvPath, browse);
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(_mkvStatus, 1, grid.RowCount++);
        AddRow("Unwanted file extensions:", _unwanted, hint: "Separated by spaces or commas. Used by the \"Remove unwanted files\" action.");
        AddRow("Extra words to remove:", _customTerms,
            hint: "One per line. Use this for site tags that do not look like a web address, for example TamilMV.\n" +
                  "They are removed from file names, folder names, MKV track names and subtitle lines.");

        var save = new Button { Text = "Save settings", AutoSize = true, MinimumSize = new Size(120, 30) };
        save.Click += (_, _) => SaveSettings();
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(save, 1, grid.RowCount++);

        page.Controls.Add(grid);
        return page;
    }

    // ----- Configuration -----

    private void LoadConfig()
    {
        try
        {
            _config = ConfigStore.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The configuration file could not be read, so default settings are shown.\n\n" + ex.Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _config = new AppConfig();
        }
        ShowConfig();
    }

    private void ShowConfig()
    {
        _loading = true;
        try
        {
            FillList(_movies, MediaKind.Movie);
            FillList(_tvShows, MediaKind.TvShow);
            _scanInterval.Value = Math.Clamp(_config.ScanIntervalSeconds, (int)_scanInterval.Minimum, (int)_scanInterval.Maximum);
            _stableSeconds.Value = Math.Clamp(_config.StableSeconds, (int)_stableSeconds.Minimum, (int)_stableSeconds.Maximum);
            _mkvPath.Text = _config.MkvToolNixPath;
            _unwanted.Text = string.Join(" ", _config.UnwantedExtensions);
            _customTerms.Lines = _config.CustomRemovalTerms.ToArray();
        }
        finally
        {
            _loading = false;
        }
        UpdateMonitoringButton();
        UpdateMkvStatus();
        ShowProblems();
    }

    private void FillList(ListView list, MediaKind kind)
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var mapping in _config.Mappings.Where(m => m.Kind == kind))
        {
            var item = new ListViewItem(mapping.Name.Length > 0 ? mapping.Name : "(no name)") { Tag = mapping, Checked = mapping.Active };
            item.SubItems.Add(mapping.SourcePath);
            item.SubItems.Add(mapping.DestinationPath.Length > 0 ? mapping.DestinationPath : "(clean up in place)");
            item.SubItems.Add(DescribeActions(mapping));
            list.Items.Add(item);
        }
        list.EndUpdate();
    }

    private static string DescribeActions(MonitorMapping mapping)
    {
        var a = mapping.Actions;
        var parts = new List<string>();
        if (a.NameCleanup) parts.Add("Names");
        if (a.TrackNameCleanup) parts.Add("Tracks");
        if (a.SetDefaultAudio) parts.Add("Audio");
        if (a.SubtitleCleanup) parts.Add("Subtitles");
        if (mapping.Kind == MediaKind.Movie && a.MoveIntoFolder) parts.Add("Folder");
        if (a.RemoveUnwantedFiles) parts.Add("Unwanted");
        return parts.Count > 0 ? string.Join(", ", parts) : "(none)";
    }

    /// <param name="refresh">False when called from a list event, where the list must not be rebuilt.</param>
    private bool Save(bool refresh = true)
    {
        try
        {
            ConfigStore.Save(_config);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The configuration could not be saved.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        if (refresh) ShowConfig();
        else ShowProblems();
        return true;
    }

    private void ShowProblems()
    {
        var problems = _config.Validate();
        _status.ForeColor = problems.Count > 0 ? InactiveColor : SystemColors.GrayText;
        _status.Text = problems.Count > 0
            ? "Please check: " + string.Join("  ", problems)
            : "Changes are saved automatically and picked up by the service within a few seconds.  Configuration: " + AppPaths.ConfigFile;
    }

    private void SaveSettings()
    {
        _config.ScanIntervalSeconds = (int)_scanInterval.Value;
        _config.StableSeconds = (int)_stableSeconds.Value;
        _config.MkvToolNixPath = _mkvPath.Text;
        _config.UnwantedExtensions = _unwanted.Text
            .Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        _config.CustomRemovalTerms = _customTerms.Lines.ToList();
        if (Save()) _status.Text = "Settings saved.";
    }

    private void UpdateMkvStatus()
    {
        var (mkvmerge, _) = MkvToolRunner.Locate(_config.MkvToolNixPath);
        _mkvStatus.Text = mkvmerge != null
            ? "Found: " + Path.GetDirectoryName(mkvmerge)
            : "MKVToolNix was not found from this account. Leave empty to search Program Files and PATH, or choose the folder that holds mkvmerge.exe and mkvpropedit.exe.";
        _mkvStatus.Margin = new Padding(3, 0, 3, 10);
    }

    // ----- Monitoring switch -----

    private void ToggleMonitoring()
    {
        _config.MonitoringEnabled = !_config.MonitoringEnabled;
        Save();
    }

    private void UpdateMonitoringButton()
    {
        _monitoringButton.Text = _config.MonitoringEnabled ? "Monitoring: ACTIVE" : "Monitoring: INACTIVE";
        _monitoringButton.BackColor = _config.MonitoringEnabled ? ActiveColor : InactiveColor;
    }

    // ----- Sources -----

    private void OnItemChecked(ListViewItem item)
    {
        if (_loading || item.Tag is not MonitorMapping mapping || mapping.Active == item.Checked) return;
        mapping.Active = item.Checked;
        Save(refresh: false);
    }

    private void AddMapping(MediaKind kind)
    {
        var mapping = new MonitorMapping { Kind = kind };
        if (kind == MediaKind.TvShow) mapping.Actions.MoveIntoFolder = false;
        using var dialog = new MappingDialog(mapping, isNew: true);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _config.Mappings.Add(mapping);
        Save();
    }

    private void EditSelected(ListView list)
    {
        if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not MonitorMapping mapping) return;
        var copy = mapping.Clone();
        using var dialog = new MappingDialog(copy, isNew: false);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var index = _config.Mappings.IndexOf(mapping);
        if (index >= 0) _config.Mappings[index] = copy;
        Save();
    }

    private void RemoveSelected(ListView list)
    {
        if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not MonitorMapping mapping) return;
        var label = mapping.Name.Length > 0 ? mapping.Name : mapping.SourcePath;
        var answer = MessageBox.Show(this, $"Remove '{label}' from the list?\n\nNo files are deleted; the folder just stops being monitored.",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        _config.Mappings.Remove(mapping);
        Save();
    }

    // ----- Service -----

    private void RefreshServiceStatus()
    {
        var status = ServiceManager.GetStatus();
        _serviceLabel.Text = "Service: " + status switch
        {
            null => "not installed",
            ServiceControllerStatus.Running => "running",
            ServiceControllerStatus.Stopped => "stopped",
            ServiceControllerStatus.StartPending => "starting...",
            ServiceControllerStatus.StopPending => "stopping...",
            _ => status.ToString()!.ToLowerInvariant(),
        };
        _serviceLabel.ForeColor = status == ServiceControllerStatus.Running ? ActiveColor : InactiveColor;
        _installButton.Text = status == null ? "Install service" : "Uninstall service";
        _startButton.Enabled = status == ServiceControllerStatus.Stopped;
        _stopButton.Enabled = status == ServiceControllerStatus.Running;
    }

    private void InstallOrUninstall()
    {
        if (ServiceManager.GetStatus() == null)
        {
            var (ok, output) = ServiceManager.Install();
            if (!ok)
            {
                MessageBox.Show(this, "The service could not be installed.\n\n" + output, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                MessageBox.Show(this,
                    "The service is installed and will start with Windows.\n\n" +
                    "IMPORTANT for network (UNC) folders: a new service runs as \"Local System\", which usually cannot open network shares. " +
                    "Click \"Windows Services...\", open \"" + AppPaths.ServiceDisplayName + "\", go to the \"Log On\" tab, choose \"This account\" " +
                    "and enter a Windows user that can open your shares. Then press \"Start service\" here.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        else
        {
            var answer = MessageBox.Show(this, "Stop and remove the Windows service? Your configuration is kept.", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            var (ok, output) = ServiceManager.Uninstall();
            if (!ok) MessageBox.Show(this, "The service could not be removed.\n\n" + output, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        RefreshServiceStatus();
    }

    private void RunServiceAction(string description, Action action)
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            MessageBox.Show(this, $"Could not {description}.\n\n{detail}\n\nIf the service fails to start right after you changed its account, " +
                "check the user name and password on the Log On tab in Windows Services.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
            RefreshServiceStatus();
        }
    }

    private void OpenShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open {target}.\n\n{ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
