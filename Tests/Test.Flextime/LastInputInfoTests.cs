using Flextime.Monitor;

namespace Test.Flextime;

public class LastInputInfoTests
{
    [Theory]
    // Shortly after boot.
    [InlineData(5_000, 4_000u, 1_000u)]
    [InlineData(5_000, 5_000u, 0u)]
    // TickCount turns negative after 24.9 days; the input came before.
    [InlineData(int.MinValue, 2_147_483_000u, 648u)]
    [InlineData(int.MinValue + 1_000, 2_147_483_000u, 1_648u)]
    // Both past the int wrap point.
    [InlineData(int.MinValue + 60_000, 2_147_483_648u, 60_000u)]
    // The 32-bit counter wraps to 0 after 49.7 days; the input came before.
    [InlineData(0, uint.MaxValue, 1u)]
    [InlineData(500, uint.MaxValue - 499, 1_000u)]
    // Just before the counter wraps.
    [InlineData(-1, uint.MaxValue - 60_000, 60_000u)]
    public void Elapsed_wraps_like_the_tick_counter(int tickCount, uint lastInput, uint expected)
    {
        Assert.Equal(expected, LastInputInfo.Elapsed(tickCount, lastInput));
    }
}
