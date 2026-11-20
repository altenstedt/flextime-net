using ConsoleAppFramework;
using Flextime;
using Flextime.Client;

Flextime.TimeZones.UseMacZoneDirectory();

ConsoleApp.Version = ConsoleApp.VersionWithHomePage;
ConsoleApp.Run(args, App.Run);

if (HomePage.AskedForHelp(args))
{
    Console.WriteLine();
    Console.WriteLine(HomePage.Line);
}
