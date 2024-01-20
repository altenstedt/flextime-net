using System.CommandLine;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Flextime;
using Inhill.Flextime.Sync;
using Inhill.Flextime.Sync.DataContracts;

var options = new Options();

var folderOption = new Option<DirectoryInfo>("--folder", () => new DirectoryInfo(Constants.MeasurementsFolder), "Folder to read measurements from");
folderOption.AddAlias("-f");

var verboseOption = new Option<bool>("--verbose", "More verbose output");
verboseOption.AddAlias("-v");

var uriOption = new Option<Uri>("--uri", () => new Uri("https://localhost:5000/", UriKind.Absolute), "URL of API");
uriOption.AddAlias("-u");

var rootCommand = new RootCommand("Flextime Sync -- Syncing data with the cloud");
rootCommand.AddGlobalOption(folderOption);
rootCommand.AddGlobalOption(verboseOption);
rootCommand.AddGlobalOption(uriOption);

var pingCommand = new Command("ping", "Ping API for version");
rootCommand.AddCommand(pingCommand);

var ping = false;
pingCommand.SetHandler((folder, verbose, uri) =>
    {
        ping = true;

        options.MeasurementsFolder = folder.FullName;
        options.Verbose = verbose;
        options.Uri = uri;
    },
    folderOption, 
    verboseOption,
    uriOption);

var listCommand = new Command("list", "List information");
rootCommand.AddCommand(listCommand);

var listSyncOption = new Option<bool>("--sync", "Sync changes that can be resolved");
var idleOption = new Option<TimeSpan>("--idle", () => TimeSpan.FromMinutes(10), "Idle limit");
var remoteOption = new Option<bool>("--remote", "List data on server ");
var computersOption = new Option<bool>("--computers", "List computers on server");
var computerOption = new Option<string>("--computer", "Computer to list on server, this computer if empty");

listCommand.AddOption(listSyncOption);
listCommand.AddOption(idleOption);
listCommand.AddOption(remoteOption);
listCommand.AddOption(computersOption);
listCommand.AddOption(computerOption);

var list = false;
listCommand.SetHandler((folder, verbose, uri, listSync, idle, remote, computers, computer) =>
    {
        list = true;

        options.MeasurementsFolder = folder.FullName;
        options.Verbose = verbose;
        options.Uri = uri;
        options.ListSync = listSync;
        options.BlocksPerDay = 0;
        options.Idle = idle;
        options.Remote = remote;
        options.Computers = computers;
        options.Computer = computer;
    },
    folderOption, 
    verboseOption,
    uriOption,
    listSyncOption,
    idleOption,
    remoteOption,
    computersOption,
    computerOption);

var result = await rootCommand.InvokeAsync(args);

if (result != 0)
{
    return result;
}

if (options.Verbose)
{
    Console.WriteLine($"Measurements folder is \"{options.MeasurementsFolder}\".");
    Console.WriteLine($"URI is \"{options.Uri}\".");
}

var computerFilePath = Path.Combine(options.MeasurementsFolder, "../computer.txt");
string computerId;
string computerName;

if (!File.Exists(computerFilePath)) {
    using var provider = RandomNumberGenerator.Create();

    var bytes = new byte[8];
    
    provider.GetBytes(bytes);

    computerId = Convert.ToHexString(bytes).ToLowerInvariant();
    computerName = Environment.MachineName;

    await File.WriteAllTextAsync(computerFilePath, $"{computerId}{Environment.NewLine}{computerName}");

    if (options.Verbose) {
        Console.WriteLine($"Computer is {computerId} {computerName} (created).");
    }
} else {
    var computerFileText = await File.ReadAllLinesAsync(computerFilePath);

    computerId = computerFileText.ElementAt(0);
    computerName = computerFileText.ElementAt(1);

    if (options.Verbose) {
        Console.WriteLine($"Computer is {computerId} {computerName}.");
    }
}

if (ping)
{
    var httpClient = new HttpClient { BaseAddress = options.Uri };

    try {
        var pingResult = await httpClient.GetFromJsonAsync<PingDataContract>("/ping");
        
        Console.WriteLine($"{pingResult?.Version} {pingResult?.Details} {pingResult?.Runtime} {pingResult?.InstanceId}");
    } catch (HttpRequestException exception) {
        Console.WriteLine($"Error contacting backend: {exception.Message}.");

        if (exception.InnerException != null) {
            Console.WriteLine($"  {exception.InnerException.Message}");
        }

        return 1;
    }
}

if (list)
{
    var httpClient = new HttpClient { BaseAddress = options.Uri };

    if (options.Computers) {
        var computers = await httpClient.GetStringAsync("/computers");

        Console.WriteLine(computers);
    }

    SummaryDataContract? remoteSummary = null;

    try {
        remoteSummary = await httpClient.GetFromJsonAsync<SummaryDataContract>($"/{computerId}/summary");
    } catch (HttpRequestException exception) {
        Console.WriteLine($"Error contacting backend: {exception.Message}.");

        if (exception.InnerException != null) {
            Console.WriteLine($"  {exception.InnerException.Message}");
        }
    }

    var byDates = options.Remote
        ? await Reader.ReadRemote(httpClient, TimeSpan.MinValue, string.IsNullOrEmpty(options.Computer) ? computerId : options.Computer)
        : Reader.ReadFiles(options.MeasurementsFolder, TimeSpan.MinValue);

    if (byDates.Count == 0)
    {
        Console.WriteLine("No data");
    } else {
        var formatter = new MeasurementsFormatter(options.Idle, options.Verbose, options.BlocksPerDay);

        foreach(var date in byDates)
        {
            var match = remoteSummary?.Items.SingleOrDefault(item => item.Date == date.Key);

            if (match == null) {
                if (options.ListSync) {
                    var measurements = new Measurements {
                        Interval = date.Value.list.First().Interval,
                        Zone = date.Value.list.First().Zone,
                        Items = date.Value.list.Select(item => item.Measurement).ToList()
                    };

                    await httpClient.PatchAsJsonAsync($"/{computerId}", measurements);
    
                    Console.WriteLine($"{formatter.SummarizeDay(date.Value.list.ToArray())} [synced]");
                } else {
                    Console.WriteLine($"{formatter.SummarizeDay(date.Value.list.ToArray())} [local only]");
                }
            } else if (match.Hash == date.Value.hash) {
                Console.WriteLine($"{formatter.SummarizeDay(date.Value.list.ToArray())} [in sync]");
            } else {
                var mismatch = Reader.ReadFiles(options.MeasurementsFolder, TimeSpan.MinValue, date.Key, match.Hash);

                if (mismatch.found) {
                    if (options.ListSync) {
                        var measurements = new Measurements {
                            Interval = mismatch.list.First().Interval,
                            Zone = mismatch.list.First().Zone,
                            Items = mismatch.list.Select(item => item.Measurement).ToList()
                        };

                        await httpClient.PatchAsJsonAsync($"/{computerId}", measurements);
                        Console.WriteLine($"{formatter.SummarizeDay(date.Value.list.ToArray())} [synced]");
    
                    } else {
                        Console.WriteLine($"{formatter.SummarizeDay(date.Value.list.ToArray())} [can sync]");
                    }
                } else {
                    Console.WriteLine($"{formatter.SummarizeDay(date.Value.list.ToArray())} [cannot sync]");
                }
            }
        }
    }
}

return 0;
