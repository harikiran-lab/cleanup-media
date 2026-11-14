using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using MediaCleanup.Core.Configuration;

namespace MediaCleanup.ConfigApp;

/// <summary>Installs, removes, starts and stops the Windows service.</summary>
internal static class ServiceManager
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static string ServiceExecutable => Path.Combine(AppContext.BaseDirectory, "MediaCleanup.Service.exe");

    /// <summary>The service state, or null when the service is not installed.</summary>
    public static ServiceControllerStatus? GetStatus()
    {
        try
        {
            using var controller = new ServiceController(AppPaths.ServiceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    public static void Start()
    {
        using var controller = new ServiceController(AppPaths.ServiceName);
        if (controller.Status == ServiceControllerStatus.Running) return;
        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, Timeout);
    }

    public static void Stop()
    {
        using var controller = new ServiceController(AppPaths.ServiceName);
        if (controller.Status == ServiceControllerStatus.Stopped) return;
        controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
    }

    /// <summary>Registers the service to start automatically and restart itself after a crash.</summary>
    public static (bool Ok, string Output) Install()
    {
        if (!File.Exists(ServiceExecutable))
            return (false, $"MediaCleanup.Service.exe was not found next to this program:\n{ServiceExecutable}");

        var create = RunSc("create", AppPaths.ServiceName,
            "binPath=", "\"" + ServiceExecutable + "\"",
            "start=", "delayed-auto",
            "DisplayName=", AppPaths.ServiceDisplayName);
        if (!create.Ok) return create;

        RunSc("description", AppPaths.ServiceName,
            "Watches download folders, cleans up media file names, MKV track names and subtitles, and moves finished downloads to their library folders.");
        RunSc("failure", AppPaths.ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000");
        return create;
    }

    public static (bool Ok, string Output) Uninstall()
    {
        try
        {
            if (GetStatus() is not null and not ServiceControllerStatus.Stopped) Stop();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException)
        {
            // Deleting a service that will not stop still marks it for removal.
        }
        return RunSc("delete", AppPaths.ServiceName);
    }

    private static (bool Ok, string Output) RunSc(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode == 0, output.Trim());
    }
}
