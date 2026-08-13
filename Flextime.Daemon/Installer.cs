using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Flextime.Daemon;

/// <summary>
/// Installs listen and sync as user services: launchd agents on macOS,
/// systemd user units on Linux, and scheduled tasks on Windows.  Everything
/// is per user; no elevation is needed.
/// </summary>
public class Installer
{
    private const string ListenLabel = "se.flextime.listen";
    private const string SyncLabel = "se.flextime.sync";

    private const string ListenUnit = "flextime-listen.service";
    private const string SyncUnit = "flextime-sync.service";
    private const string SyncTimer = "flextime-sync.timer";

    private const string ListenTask = "Flextime Listen";
    private const string SyncTask = "Flextime Sync";

    [DllImport("libc")]
    private static extern uint getuid();

    public async Task<int> Install(string? timeZone, TimeSpan every)
    {
        if (every <= TimeSpan.Zero)
        {
            Console.Error.WriteLine("The sync interval must be positive.");
            return 1;
        }

        if (string.IsNullOrEmpty(timeZone))
        {
            if (OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("The --time-zone option is required on Windows.");
                return 1;
            }
        }
        else if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
        {
            Console.Error.WriteLine($"Time zone {timeZone} not found on this system.");
            return 1;
        }

        var executable = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executable))
        {
            Console.Error.WriteLine("Cannot determine the path of this executable.");
            return 1;
        }

        List<string> listenCommand = [executable, "listen"];

        if (!string.IsNullOrEmpty(timeZone))
        {
            listenCommand.AddRange(["--time-zone", timeZone]);
        }

        List<string> syncCommand = [executable, "sync", "--once"];

        if (OperatingSystem.IsMacOS())
        {
            return await InstallMacOS(listenCommand, syncCommand, every);
        }

        if (OperatingSystem.IsLinux())
        {
            return await InstallLinux(listenCommand, syncCommand, every);
        }

        if (OperatingSystem.IsWindows())
        {
            return await InstallWindows(listenCommand, syncCommand, every);
        }

        Console.Error.WriteLine($"OS {RuntimeInformation.OSDescription} is not supported.");
        return 1;
    }

    public async Task<int> Uninstall()
    {
        if (OperatingSystem.IsMacOS())
        {
            return await UninstallMacOS();
        }

        if (OperatingSystem.IsLinux())
        {
            return await UninstallLinux();
        }

        if (OperatingSystem.IsWindows())
        {
            return await UninstallWindows();
        }

        Console.Error.WriteLine($"OS {RuntimeInformation.OSDescription} is not supported.");
        return 1;
    }

    private static async Task<int> InstallMacOS(List<string> listenCommand, List<string> syncCommand, TimeSpan every)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agents = Path.Combine(home, "Library", "LaunchAgents");
        var logs = Path.Combine(home, "Library", "Logs", "Flextime");

        Directory.CreateDirectory(agents);
        Directory.CreateDirectory(logs);

        var domain = $"gui/{getuid()}";

        foreach (var (label, command, keepAlive, interval, log) in new[]
                 {
                     (ListenLabel, listenCommand, true, (TimeSpan?)null, Path.Combine(logs, "listen.log")),
                     (SyncLabel, syncCommand, false, every, Path.Combine(logs, "sync.log")),
                 })
        {
            var path = Path.Combine(agents, $"{label}.plist");

            await File.WriteAllTextAsync(path, ServiceFiles.LaunchAgentPlist(label, command, log, keepAlive, interval));

            Console.WriteLine($"Wrote {path}");

            // Unload a previous installation first so that install doubles
            // as upgrade.  Fails harmlessly when nothing is loaded.
            await Run("launchctl", "bootout", $"{domain}/{label}");

            var (code, error) = await Run("launchctl", "bootstrap", domain, path);

            if (code != 0)
            {
                Console.Error.WriteLine($"launchctl bootstrap failed: {error}");
                return 1;
            }
        }

        Console.WriteLine($"Logs are written to {logs}");
        Console.WriteLine($"Check status with: launchctl print {domain}/{ListenLabel}");
        return 0;
    }

    private static async Task<int> UninstallMacOS()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agents = Path.Combine(home, "Library", "LaunchAgents");
        var domain = $"gui/{getuid()}";

        foreach (var label in new[] { ListenLabel, SyncLabel })
        {
            await Run("launchctl", "bootout", $"{domain}/{label}");

            var path = Path.Combine(agents, $"{label}.plist");

            if (File.Exists(path))
            {
                File.Delete(path);
                Console.WriteLine($"Removed {path}");
            }
        }

        Console.WriteLine("Measurements, tokens, and logs are kept.");
        return 0;
    }

    private static async Task<int> InstallLinux(List<string> listenCommand, List<string> syncCommand, TimeSpan every)
    {
        var units = SystemdUnitDirectory();

        Directory.CreateDirectory(units);

        foreach (var (name, content) in new[]
                 {
                     (ListenUnit, ServiceFiles.SystemdListenService(listenCommand)),
                     (SyncUnit, ServiceFiles.SystemdSyncService(syncCommand)),
                     (SyncTimer, ServiceFiles.SystemdSyncTimer(every)),
                 })
        {
            var path = Path.Combine(units, name);

            await File.WriteAllTextAsync(path, content);

            Console.WriteLine($"Wrote {path}");
        }

        var (reloadCode, reloadError) = await Run("systemctl", "--user", "daemon-reload");

        if (reloadCode != 0)
        {
            Console.Error.WriteLine($"systemctl daemon-reload failed: {reloadError}");
            return 1;
        }

        var (code, error) = await Run("systemctl", "--user", "enable", "--now", ListenUnit, SyncTimer);

        if (code != 0)
        {
            Console.Error.WriteLine($"systemctl enable failed: {error}");
            return 1;
        }

        Console.WriteLine($"Check status with: systemctl --user status {ListenUnit}");
        Console.WriteLine($"Logs with: journalctl --user -u {ListenUnit}");
        return 0;
    }

    private static async Task<int> UninstallLinux()
    {
        var units = SystemdUnitDirectory();

        await Run("systemctl", "--user", "disable", "--now", ListenUnit, SyncTimer);

        foreach (var name in new[] { ListenUnit, SyncUnit, SyncTimer })
        {
            var path = Path.Combine(units, name);

            if (File.Exists(path))
            {
                File.Delete(path);
                Console.WriteLine($"Removed {path}");
            }
        }

        await Run("systemctl", "--user", "daemon-reload");

        Console.WriteLine("Measurements and tokens are kept.");
        return 0;
    }

    private static async Task<int> InstallWindows(List<string> listenCommand, List<string> syncCommand, TimeSpan every)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";

        foreach (var (name, xml) in new[]
                 {
                     (ListenTask, ServiceFiles.WindowsTaskXml("Flextime listen", user, listenCommand, null, null)),
                     (SyncTask, ServiceFiles.WindowsTaskXml("Flextime sync", user, syncCommand, every, TimeSpan.FromHours(1))),
                 })
        {
            var path = Path.GetTempFileName();

            try
            {
                // Task Scheduler expects task XML in UTF-16.
                await File.WriteAllTextAsync(path, xml, Encoding.Unicode);

                var (code, error) = await Run("schtasks", "/Create", "/TN", name, "/XML", path, "/F");

                if (code != 0)
                {
                    Console.Error.WriteLine($"schtasks /Create failed for {name}: {error}");
                    return 1;
                }

                Console.WriteLine($"Registered scheduled task {name}");
            }
            finally
            {
                File.Delete(path);
            }

            // The logon trigger fires at the next logon; start now as well.
            await Run("schtasks", "/Run", "/TN", name);
        }

        Console.WriteLine("The sync repetition schedule takes full effect at the next logon.");
        Console.WriteLine($"Check status with: schtasks /Query /TN \"{ListenTask}\"");
        return 0;
    }

    private static async Task<int> UninstallWindows()
    {
        foreach (var name in new[] { ListenTask, SyncTask })
        {
            await Run("schtasks", "/End", "/TN", name);

            var (code, _) = await Run("schtasks", "/Delete", "/TN", name, "/F");

            if (code == 0)
            {
                Console.WriteLine($"Removed scheduled task {name}");
            }
        }

        Console.WriteLine("Measurements and tokens are kept.");
        return 0;
    }

    private static string SystemdUnitDirectory()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        var root = string.IsNullOrEmpty(config)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : config;

        return Path.Combine(root, "systemd", "user");
    }

    private static async Task<(int code, string error)> Run(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);

            if (process == null)
            {
                return (1, $"Failed to start {fileName}.");
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(output, error, process.WaitForExitAsync());

            return (process.ExitCode, error.Result.Trim());
        }
        catch (Win32Exception exception)
        {
            return (1, $"Failed to start {fileName}: {exception.Message}");
        }
    }
}
