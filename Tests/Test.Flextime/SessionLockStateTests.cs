using System.Runtime.InteropServices;
using Flextime.Monitor;

namespace Test.Flextime;

public class SessionLockStateTests
{
    [Theory]
    // WTS_SESSIONSTATE_LOCK is 0, so a zeroed buffer reads as locked;
    // the level check is what guards against that.
    [InlineData(1, 0, true)]
    [InlineData(1, 1, false)]
    // WTS_SESSIONSTATE_UNKNOWN.
    [InlineData(1, -1, null)]
    [InlineData(1, 2, null)]
    // Only level 1 is defined.
    [InlineData(0, 0, null)]
    [InlineData(2, 1, null)]
    public void Session_flags_give_the_lock_state(int level, int sessionFlags, bool? expected)
    {
        Assert.Equal(expected, SessionLockState.FromSessionInfo(level, sessionFlags));
    }

    [Fact]
    public void Unknown_off_Windows()
    {
        Assert.SkipWhen(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Reads the real session on Windows.");

        Assert.Null(SessionLockState.IsLocked());
    }
}
