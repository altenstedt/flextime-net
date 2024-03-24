using System.CommandLine;
using Flextime;
using Flextime.Client;

var folderOption = new Option<DirectoryInfo>("--folder", () => new DirectoryInfo(Constants.MeasurementsFolder), "Folder to read measurements from");
folderOption.AddAlias("-f");

var verboseOption = new Option<bool>("--verbose", "More verbose output");
verboseOption.AddAlias("-v");

var splitWeekOption = new Option<bool>("--split-week", "Split weeks with a new line");
splitWeekOption.AddAlias("-s");

var blocksPerDayOption = new Option<int>("--blocks", "Number of blocks per day");

var idleOption = new Option<TimeSpan>("--idle", () => TimeSpan.FromMinutes(10), "Idle limit");

var sinceOption = new Option<TimeSpan>("--since", "Print measurements since");

var rootCommand = new RootCommand("Flextime -- tracking working hours");
rootCommand.AddOption(folderOption);
rootCommand.AddOption(verboseOption);
rootCommand.AddOption(splitWeekOption);
rootCommand.AddOption(blocksPerDayOption);
rootCommand.AddOption(idleOption);
rootCommand.AddOption(sinceOption);

rootCommand.SetHandler((folder, verbose, splitWeek, blocksPerDay, idle, since) =>
    {
        var options = new Options
        {
            MeasurementsFolder = folder.FullName,
            Verbose = verbose,
            SplitWeek = splitWeek,
            BlocksPerDay = blocksPerDay,
            Idle = idle,
            Since = since
        };

        var print = new Print(options);
        print.PrintMeasurements();
    },
    folderOption, 
    verboseOption,
    splitWeekOption,
    blocksPerDayOption,
    idleOption,
    sinceOption);

return await rootCommand.InvokeAsync(args);
