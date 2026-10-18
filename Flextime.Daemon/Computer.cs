using System.Security.Cryptography;

namespace Flextime.Daemon;

public class Computer
{
    private readonly string? hostname;

    public Computer()
    {
    }

    /// <summary>A computer known without its file, for tests.</summary>
    public Computer(string id, string hostname)
    {
        Id = id;
        this.hostname = hostname;
    }

    public string? Id { get; private set; }

    /// <summary>
    /// What the computer calls itself now, read every time it is asked
    /// for.  The name the user gives the computer lives on the server.
    /// </summary>
    public string Hostname => hostname ?? Daemon.Hostname.Read();

    public string MeasurementsFolder => Constants.MeasurementsFolder;
    
    public async Task Initialize()
    {
        var computerFilePath = Path.Combine(MeasurementsFolder, "../computer.txt");

        // Every command starts here, so a folder created before it was
        // made private is tightened by the next one run.
        PrivateFolder.Ensure(Path.GetDirectoryName(computerFilePath)!);

        if (File.Exists(computerFilePath))
        {
            var lines = await File.ReadAllLinesAsync(computerFilePath);

            // The second line is the host name on the first run.  It is
            // no longer read, since it never followed a rename, but it is
            // still written: an older flextimed takes a file without it
            // for damaged, and would give the computer a new id.
            if (lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[0]) && !string.IsNullOrWhiteSpace(lines[1]))
            {
                Id = lines[0];

                return;
            }

            // The file is damaged; fall through and recreate it.
        }

        using var provider = RandomNumberGenerator.Create();

        var bytes = new byte[8];

        provider.GetBytes(bytes);

        Id = Convert.ToHexString(bytes).ToLowerInvariant();

        await File.WriteAllTextAsync(computerFilePath, $"{Id}{Environment.NewLine}{Environment.MachineName}");
    }
}
