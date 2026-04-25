using ConsoleAppFramework;
using Flextime.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var (_, _, refreshToken) = await TokenStorage.Read();

var deviceCode = new DeviceCode();
await deviceCode.Initialize();

var computer = new Computer();
await computer.Initialize();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(computer);
builder.Services.AddSingleton(deviceCode);
builder.Services.AddSingleton<Sync>();
builder.Services.AddSingleton<PrintInfo>();
builder.Services.AddApiHttpClient(refreshToken);

builder.Logging.AddFilter("Flextime",
    Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("FLEXTIME_LOG_LEVEL"), out var logLevel)
        ? logLevel
        : LogLevel.Information);

builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Polly", LogLevel.None); // We print manually instead.

builder.Logging.AddSystemdConsole();
builder.Logging.AddSimpleConsole(formatterOptions =>
{
    formatterOptions.SingleLine = true;
    formatterOptions.TimestampFormat = "HH:mm:ss ";
});

if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog();
}

var app = builder.ToConsoleAppBuilder();

app.Add<DaemonCommands>();

await app.RunAsync(args);
