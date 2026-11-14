using MediaCleanup.Core.Configuration;
using MediaCleanup.Core.Processing;

namespace MediaCleanup.Service;

/// <summary>Scans the configured sources on a timer and reloads the configuration whenever it changes.</summary>
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _log;
    private AppConfig _config = new();
    private DateTime _configStamp = DateTime.MinValue;
    private bool _configLoaded;
    private string? _lastConfigError;
    private bool? _lastEnabled;

    public Worker(ILogger<Worker> logger) => _log = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Media Cleanup Service started. Configuration: {Config}", AppPaths.ConfigFile);
        var engine = new MonitorEngine(_log, new ProcessedLedger(AppPaths.LedgerFile));

        while (!stoppingToken.IsCancellationRequested)
        {
            ReloadConfigIfChanged();
            try
            {
                await engine.ScanOnceAsync(_config, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unexpected error during a scan; the service keeps running.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.ScanIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.LogInformation("Media Cleanup Service stopped.");
    }

    private void ReloadConfigIfChanged()
    {
        try
        {
            var file = AppPaths.ConfigFile;
            var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
            if (_configLoaded && stamp == _configStamp) return;

            _config = ConfigStore.Load(file);
            _configStamp = stamp;
            _configLoaded = true;
            _lastConfigError = null;

            var active = _config.Mappings.Count(m => m.Active);
            _log.LogInformation("Configuration loaded: {Active} of {Total} source(s) active, scanning every {Interval}s, files must be unchanged for {Stable}s.",
                active, _config.Mappings.Count, _config.ScanIntervalSeconds, _config.StableSeconds);
            foreach (var problem in _config.Validate())
                _log.LogWarning("Configuration problem: {Problem}", problem);
            if (_lastEnabled != _config.MonitoringEnabled)
            {
                _log.LogInformation(_config.MonitoringEnabled ? "Monitoring is ACTIVE." : "Monitoring is INACTIVE; nothing will be touched.");
                _lastEnabled = _config.MonitoringEnabled;
            }
        }
        catch (Exception ex)
        {
            // Keep running on the last good configuration, and say so only once per distinct error.
            if (_lastConfigError != ex.Message)
            {
                _log.LogError("Could not read the configuration ({Error}). Continuing with the previous settings.", ex.Message);
                _lastConfigError = ex.Message;
            }
        }
    }
}
