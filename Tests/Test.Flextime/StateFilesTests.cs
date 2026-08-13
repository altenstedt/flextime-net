using Flextime.Daemon;

namespace Test.Flextime;

public class StateFilesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-state-files-tests-{Guid.NewGuid():N}");

    public StateFilesTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void WriteAndReadRoundTrips()
    {
        var path = Path.Combine(folder, "listen.txt");

        StateFiles.Write(path, "Europe/Stockholm");

        Assert.Equal(new[] { "Europe/Stockholm" }, StateFiles.TryRead(path));
    }

    [Fact]
    public void WriteReplacesPreviousContent()
    {
        var path = Path.Combine(folder, "install.txt");

        StateFiles.Write(path, "00:20:00", "2026-08-13");
        StateFiles.Write(path, "00:10:00");

        Assert.Equal(new[] { "00:10:00" }, StateFiles.TryRead(path));
    }

    [Fact]
    public void MissingFileReadsAsNull()
    {
        Assert.Null(StateFiles.TryRead(Path.Combine(folder, "missing.txt")));
    }

    [Fact]
    public void WriteCreatesMissingDirectory()
    {
        var path = Path.Combine(folder, "missing", "listen.txt");

        StateFiles.Write(path, "UTC");

        Assert.Equal(new[] { "UTC" }, StateFiles.TryRead(path));
    }

    [Fact]
    public void DeleteIsIdempotent()
    {
        var path = Path.Combine(folder, "install.txt");

        StateFiles.Write(path, "00:20:00");

        StateFiles.Delete(path);
        StateFiles.Delete(path);

        Assert.Null(StateFiles.TryRead(path));
    }
}
