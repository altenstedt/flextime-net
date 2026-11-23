using Flextime;
using ConsoleAppFramework;
using Flextime.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Flextime.TimeZones.UseMacZoneDirectory();

var (accessToken, expires, refreshToken) = await TokenStorage.Read();

var deviceCode = new DeviceCode();
await deviceCode.Initialize();

var computer = new Computer();
await computer.Initialize();

// Read here as well as by the global option below: the console logger
// takes its colour when the host is built, before the options are.
Colour.NoColorOption = args.TakeWhile(arg => arg != "--")
    .Any(arg => arg.Equals(Colour.Option, StringComparison.OrdinalIgnoreCase));

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(computer);
builder.Services.AddSingleton(deviceCode);
builder.Services.AddSingleton<Sync>();
builder.Services.AddSingleton<PrintInfo>();
builder.Services.AddSingleton<PrintData>();
builder.Services.AddSingleton<Export>();
builder.Services.AddSingleton<PolicyClient>();
builder.Services.AddSingleton<Installer>();
builder.Services.AddSingleton<LocationClient>();
builder.Services.AddSingleton<ComputerClient>();
builder.Services.AddSingleton<PauseCommand>();
builder.Services.AddApiHttpClient(accessToken, expires, refreshToken);

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
    formatterOptions.ColorBehavior = Colour.Wanted
        ? Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Default
        : Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Disabled;
});

if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog();
}

var app = builder.ToConsoleAppBuilder();

app.ConfigureGlobalOptions((ref ConsoleApp.GlobalOptionsBuilder options) =>
{
    // Declared for the help, and so the commands do not see it.
    return options.AddGlobalOption<bool>(Colour.Option, Colour.Description);
});

app.UseFilter<UnknownCommandFilter>();
app.Add<DaemonCommands>();

await app.RunAsync(args);

// The root help ends with the home page.  A lone --help never reaches a
// filter, and a first argument that is not an option names a command.
if (args is [var first, ..] && first.StartsWith('-') && HomePage.AskedForHelp(args))
{
    Console.WriteLine();
    Console.WriteLine(HomePage.Line);
}
