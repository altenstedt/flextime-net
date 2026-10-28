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
    public void IsHeldReflectsLockState()
    {
        var path = Path.Combine(folder, "listen.lock");

        Assert.False(SingleInstance.IsHeld(path));

        using var held = SingleInstance.TryAcquire(path);

        Assert.True(SingleInstance.IsHeld(path));
    }

    [Fact]
    public void ProbeDoesNotHoldTheLock()
    {
        var path = Path.Combine(folder, "listen.lock");

        Assert.False(SingleInstance.IsHeld(path));

        using var held = SingleInstance.TryAcquire(path);

        Assert.NotNull(held);
    }

    [Fact]
    public async Task WaitingAcquireGetsALockReleasedMeanwhile()
    {
        var path = Path.Combine(folder, "listen.lock");

        // Like the probe in IsHeld, only held for longer.
        var probe = SingleInstance.TryAcquire(path);

        Assert.NotNull(probe);

        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
            probe!.Dispose();
        }, TestContext.Current.CancellationToken);

        using var held = await SingleInstance.TryAcquire(path, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await release;

        Assert.NotNull(held);
    }

    [Fact]
    public async Task WaitingAcquireGivesUpWhileHeld()
    {
        var path = Path.Combine(folder, "listen.lock");

        using var first = SingleInstance.TryAcquire(path);

        Assert.NotNull(first);
        Assert.Null(await SingleInstance.TryAcquire(path, TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitingAcquireReturnsAtOnceWhenFree()
    {
        var path = Path.Combine(folder, "listen.lock");

        using var held = await SingleInstance.TryAcquire(path, TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.NotNull(held);
    }

    [Fact]
    public void CreatesMissingDirectory()
    {
        using var held = SingleInstance.TryAcquire(Path.Combine(folder, "missing", "listen.lock"));

        Assert.NotNull(held);
    }
}
