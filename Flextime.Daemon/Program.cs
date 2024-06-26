using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using Flextime.Daemon;

// https://github.com/dotnet/command-line-api/labels/Powderhouse
// https://github.com/dotnet/command-line-api/issues/440#issuecomment-2024850186
// https://github.com/dotnet/command-line-api/issues/556
// https://github.com/dotnet/command-line-api/issues/2338
var infoOption = new Option<bool>("--info", "Display information and exit.");

var rootCommand = new RootCommand("Flextime -- tracking working hours") {
    Handler = CommandHandler.Create<IHost>(host => host.WaitForShutdown())
};

rootCommand.AddGlobalOption(infoOption);

var logInCommand = new Command("login", "Log in to remote") {
    Handler = CommandHandler.Create<IHost>(host => host.WaitForShutdown())
};

rootCommand.AddCommand(logInCommand);

var syncCommand = new Command("sync", "Synchronize data with remote") {
    Handler = CommandHandler.Create<IHost>(host => host.WaitForShutdown())
};
var onceOption = new Option<bool>("--once", "Sync data once with remote.");
syncCommand.AddOption(onceOption);
var everyOption = new Option<TimeSpan?>("--every", "Sync data recurring with remote.");
syncCommand.AddOption(everyOption);

rootCommand.AddCommand(syncCommand);
var listenCommand = new Command("listen", "Listen to events on device") {
    Handler = CommandHandler.Create<IHost>(host => host.WaitForShutdown())
};

var timeZoneOption = new Option<string>("--time-zone", "Time zone used.");
timeZoneOption.AddAlias("-t");

var ignoreSessionLockedOption = new Option<bool>("--ignore-session-locked", "Keep tracking measurements when the computer is locked");
var logSummaryIntervalOption = new Option<TimeSpan>("--log-summary-interval", () => TimeSpan.FromHours(1), "Log summary interval");

listenCommand.AddOption(timeZoneOption);
listenCommand.AddOption(ignoreSessionLockedOption);
listenCommand.AddOption(logSummaryIntervalOption);

rootCommand.AddCommand(listenCommand);

var (_, _, refreshToken) = await TokenStorage.Read();

var deviceCode = new DeviceCode();
await deviceCode.Initialize();

var computer = new Computer();
await computer.Initialize();

var parser = new CommandLineBuilder(rootCommand)
    .UseDefaults()
    .UseHost(host =>
    {
        host.ConfigureServices(services =>
        {
            services.AddSingleton(computer);
            services.AddSingleton(deviceCode);
            services.AddSingleton<Sync>();
            
            services.AddSingleton<PrintInfo>();
            services.AddApiHttpClient(refreshToken);
            
            services.AddOptions<TimeZoneOptions>()
                .Configure<ParseResult>((options, parseResult) => {
                    options.TimeZone = parseResult.GetValueForOption(timeZoneOption);
                });

            services.AddOptions<OnceOptions>()
                .Configure<ParseResult>((options, parseResult) => {
                    options.Once = parseResult.GetValueForOption(onceOption);
                });
            services.AddOptions<EveryOptions>()
                .Configure<ParseResult>((options, parseResult) => {
                    options.Every = parseResult.GetValueForOption(everyOption);
                });
            
            services.AddOptions<IgnoreSessionLockedOptions>()
                .Configure<ParseResult>((options, parseResult) => {
                    options.IgnoreSessionLocked = parseResult.GetValueForOption(ignoreSessionLockedOption);
                });
            
            services.AddOptions<LogSummaryIntervalOptions>()
                .Configure<ParseResult>((options, parseResult) => {
                    options.Interval = parseResult.GetValueForOption(logSummaryIntervalOption);
                });
            
            services.AddOptions<CommandOptions>()
                .Configure<ParseResult>((options, parseResult) => {
                    options.SyncInvoked = parseResult.CommandResult.Command == syncCommand;
                    options.ListenInvoked = parseResult.CommandResult.Command == listenCommand;
                    options.LogInInvoked = parseResult.CommandResult.Command == logInCommand;
                    options.InfoInvoked = parseResult.RootCommandResult.GetValueForOption(infoOption);
                });

            services.AddHostedService<Worker>();
            services.AddLogging(builder =>
            {
                builder.AddFilter("Flextime",
                    Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("FLEXTIME_LOG_LEVEL"), out var logLevel)
                        ? logLevel
                        : LogLevel.Information);

                builder.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
                builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
                builder.AddFilter("Polly", LogLevel.None); // We print manually instead.

                builder.AddSystemdConsole();
                builder.AddSimpleConsole(formatterOptions =>
                {
                    formatterOptions.SingleLine = true;
                    formatterOptions.TimestampFormat = "HH:mm:ss ";
                });

                if (OperatingSystem.IsWindows())
                {
                    builder.AddEventLog();
                }
            });
        });
    })
    .Build();

return await parser.InvokeAsync(args);
