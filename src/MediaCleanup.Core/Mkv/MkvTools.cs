using System.Diagnostics;
using System.Text;

namespace MediaCleanup.Core.Mkv;

public sealed record ToolResult(int ExitCode, string Output, string Errors = "")
{
    public string AllText => string.IsNullOrWhiteSpace(Errors) ? Output.Trim() : (Output + "\n" + Errors).Trim();
}

/// <summary>Runs an MKVToolNix program. Abstracted so the cleanup logic can be tested without the tools.</summary>
public interface IMkvToolRunner
{
    /// <summary>True when both mkvmerge and mkvpropedit were found.</summary>
    bool IsAvailable { get; }
    string? Location { get; }
    Task<ToolResult> RunMkvMergeAsync(IReadOnlyList<string> arguments, CancellationToken ct);
    Task<ToolResult> RunMkvPropEditAsync(IReadOnlyList<string> arguments, CancellationToken ct);
}

public sealed class MkvToolRunner : IMkvToolRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);
    private readonly string? _mkvmerge;
    private readonly string? _mkvpropedit;

    public MkvToolRunner(string? configuredFolder)
    {
        (_mkvmerge, _mkvpropedit) = Locate(configuredFolder);
    }

    public bool IsAvailable => _mkvmerge != null && _mkvpropedit != null;
    public string? Location => _mkvmerge == null ? null : Path.GetDirectoryName(_mkvmerge);

    public Task<ToolResult> RunMkvMergeAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAsync(_mkvmerge ?? throw new InvalidOperationException("mkvmerge was not found."), arguments, ct);

    public Task<ToolResult> RunMkvPropEditAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunAsync(_mkvpropedit ?? throw new InvalidOperationException("mkvpropedit was not found."), arguments, ct);

    /// <summary>Looks in the configured folder, then the standard install folders, then PATH.</summary>
    public static (string? MkvMerge, string? MkvPropEdit) Locate(string? configuredFolder)
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var folders = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredFolder)) folders.Add(configuredFolder.Trim().Trim('"'));
        if (OperatingSystem.IsWindows())
        {
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MKVToolNix"));
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MKVToolNix"));
            folders.Add(@"C:\Program Files\MKVToolNix");
        }
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        folders.AddRange(pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (var folder in folders)
        {
            try
            {
                var merge = Path.Combine(folder, "mkvmerge" + exe);
                var prop = Path.Combine(folder, "mkvpropedit" + exe);
                if (File.Exists(merge) && File.Exists(prop)) return (merge, prop);
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry; keep looking.
            }
        }
        return (null, null);
    }

    private static async Task<ToolResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Ask for UTF-8 output so track names in any language survive the round trip.
        startInfo.ArgumentList.Add("--output-charset");
        startInfo.ArgumentList.Add("UTF-8");
        if (!OperatingSystem.IsWindows())
        {
            // Windows always passes arguments as Unicode; elsewhere the tools assume the locale's charset.
            startInfo.ArgumentList.Add("--command-line-charset");
            startInfo.ArgumentList.Add("UTF-8");
        }
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within {Timeout.TotalMinutes:0} minutes.");
        }

        var output = await stdout.ConfigureAwait(false);
        var errors = await stderr.ConfigureAwait(false);
        return new ToolResult(process.ExitCode, output, errors);
    }
}
