using Flextime;

namespace Test.Flextime;

public class PrivateFolderTests : IDisposable
{
    private const UnixFileMode UserOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-private-folder-tests-{Guid.NewGuid():N}");

    public PrivateFolderTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void CreatesFolderPrivate()
    {
        var path = Path.Combine(folder, "Flextime", "measurements");

        PrivateFolder.Ensure(path);

        Assert.True(Directory.Exists(path));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UserOnly, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void TightensLooseFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(folder, "Flextime");

        Directory.CreateDirectory(path, UserOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        PrivateFolder.Ensure(path);

        Assert.Equal(UserOnly, File.GetUnixFileMode(path));
    }

    [Fact]
    public void TightensFolderNamedThroughParent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The daemon names its folder as measurements/.. everywhere.
        var path = Path.Combine(folder, "Flextime");

        Directory.CreateDirectory(Path.Combine(path, "measurements"));
        File.SetUnixFileMode(path, UserOnly | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        PrivateFolder.Ensure(Path.Combine(path, "measurements", ".."));

        Assert.Equal(UserOnly, File.GetUnixFileMode(path));
    }
}
