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

        if (!File.Exists(computerFilePath))
        {
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
        else
        {
            var computerFileText = await File.ReadAllLinesAsync(computerFilePath);

            Id = computerFileText.ElementAt(0);
            Name = computerFileText.ElementAt(1);
        }
    }
}