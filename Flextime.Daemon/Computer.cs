using System.Security.Cryptography;

namespace Flextime.Daemon;

public class Computer
{
    public string? Id { get; private set; }
    public string? Name { get; private set; }

    public string MeasurementsFolder => Constants.MeasurementsFolder;
    
    public async Task Initialize()
    {
        var computerFilePath = Path.Combine(MeasurementsFolder, "../computer.txt");

        if (File.Exists(computerFilePath))
        {
            var lines = await File.ReadAllLinesAsync(computerFilePath);

            if (lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[0]) && !string.IsNullOrWhiteSpace(lines[1]))
            {
                Id = lines[0];
                Name = lines[1];

                return;
            }

            // The file is damaged; fall through and recreate it.
        }

        using var provider = RandomNumberGenerator.Create();

        var bytes = new byte[8];

        provider.GetBytes(bytes);

        Id = Convert.ToHexString(bytes).ToLowerInvariant();
        Name = Environment.MachineName;

        var directoryName = Path.GetDirectoryName(computerFilePath);
        if (directoryName != null)
        {
            // Create the directory if it does not exist.  This happens
            // if this command is the first command you run on this machine.
            Directory.CreateDirectory(directoryName);
        }

        await File.WriteAllTextAsync(computerFilePath, $"{Id}{Environment.NewLine}{Name}");
    }
}