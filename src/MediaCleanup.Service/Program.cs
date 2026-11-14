using MediaCleanup.Core.Configuration;
using MediaCleanup.Service;

// Runs as a Windows service when started by the Service Control Manager, and as a normal console
// program (Ctrl+C to stop) when started from a terminal, which is handy for a first test.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = AppPaths.ServiceName);
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddProvider(new FileLoggerProvider(AppPaths.LogDirectory));
builder.Services.AddHostedService<Worker>();

builder.Build().Run();
