using Flextime.Daemon;

namespace Test.Flextime;

public class SyncWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 8, 20);

    [Fact]
    public void Missing_Stamp_Is_Due()
    {
        Assert.Null(Sync.DueWindow(DateTimeOffset.MinValue, Now, Today));
    }

    [Fact]
    public void Stamp_Older_Than_A_Day_Is_Due()
    {
        Assert.Null(Sync.DueWindow(Now.AddHours(-25), Now, Today));
    }

    [Fact]
    public void Fresh_Stamp_Gives_A_Window()
    {
        Assert.Equal(new DateOnly(2026, 8, 13), Sync.DueWindow(Now.AddHours(-1), Now, Today));
    }

    [Fact]
    public void Stamp_Exactly_A_Day_Old_Is_Due()
    {
        Assert.Null(Sync.DueWindow(Now.AddDays(-1), Now, Today));
    }

    [Fact]
    public void Stamp_In_The_Future_Is_Due()
    {
        // A clock that was wrong when the stamp was written must not
        // switch the full compare off until real time catches up.
        Assert.Null(Sync.DueWindow(Now.AddYears(1), Now, Today));
    }
}
