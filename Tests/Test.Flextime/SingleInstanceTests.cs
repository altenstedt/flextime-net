using Flextime.Daemon;

namespace Test.Flextime;

public class SingleInstanceTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-single-instance-tests-{Guid.NewGuid():N}");

    public SingleInstanceTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void SecondAcquireFailsWhileHeld()
    {
        var path = Path.Combine(folder, "listen.lock");

        using var first = SingleInstance.TryAcquire(path);

        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(path));
    }

    [Fact]
    public void ReleasedLockCanBeAcquiredAgain()
    {
        var path = Path.Combine(folder, "listen.lock");

        var first = SingleInstance.TryAcquire(path);

        Assert.NotNull(first);
        first!.Dispose();

        using var second = SingleInstance.TryAcquire(path);

        Assert.NotNull(second);
    }

    [Fact]
    public void DifferentPathsDoNotConflict()
    {
        using var listen = SingleInstance.TryAcquire(Path.Combine(folder, "listen.lock"));
        using var sync = SingleInstance.TryAcquire(Path.Combine(folder, "sync.lock"));

        Assert.NotNull(listen);
        Assert.NotNull(sync);
    }

    [Fact]
    public void CreatesMissingDirectory()
    {
        using var held = SingleInstance.TryAcquire(Path.Combine(folder, "missing", "listen.lock"));

        Assert.NotNull(held);
    }
}
