using Flextime;

namespace Test.Flextime;

public class MonthsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static (DateOnly From, DateOnly To) Parse(string text)
    {
        Assert.True(Months.TryParse(text, Now, out var from, out var to), $"could not read {text}");

        return (from, to);
    }

    [Theory]
    [InlineData("6")]
    [InlineData("06")]
    [InlineData("jun")]
    [InlineData("June")]
    [InlineData("2026-06")]
    [InlineData("2026-jun")]
    public void AMonthRunsFirstToLast(string text)
    {
        var (from, to) = Parse(text);

        Assert.Equal(new DateOnly(2026, 6, 1), from);
        Assert.Equal(new DateOnly(2026, 6, 30), to);
    }

    [Theory]
    [InlineData("6-8")]
    [InlineData("jun-aug")]
    [InlineData("2026-jun-aug")]
    public void ARangeSpansWholeMonths(string text)
    {
        var (from, to) = Parse(text);

        Assert.Equal(new DateOnly(2026, 6, 1), from);

        // August has not finished, so it stops at today.
        Assert.Equal(new DateOnly(2026, 8, 21), to);
    }

    [Fact]
    public void FebruaryKnowsAboutLeapYears()
    {
        Assert.True(Months.TryParse("2024-02", Now, out _, out var to));
        Assert.Equal(new DateOnly(2024, 2, 29), to);

        Assert.True(Months.TryParse("2026-02", Now, out _, out var common));
        Assert.Equal(new DateOnly(2026, 2, 28), common);
    }

    [Fact]
    public void ThisMonthEndsToday()
    {
        var (from, to) = Parse("this");

        Assert.Equal(new DateOnly(2026, 8, 1), from);
        Assert.Equal(new DateOnly(2026, 8, 21), to);
    }

    [Fact]
    public void LastMonthIsWhole()
    {
        var (from, to) = Parse("last");

        Assert.Equal(new DateOnly(2026, 7, 1), from);
        Assert.Equal(new DateOnly(2026, 7, 31), to);
    }

    [Fact]
    public void LastMonthInJanuaryIsTheYearBefore()
    {
        var january = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.True(Months.TryParse("last", january, out var from, out var to));
        Assert.Equal(new DateOnly(2025, 12, 1), from);
        Assert.Equal(new DateOnly(2025, 12, 31), to);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("13")]
    [InlineData("8-6")]
    [InlineData("next")]
    [InlineData("smarch")]
    [InlineData("2026-13")]
    public void NonsenseIsRefused(string text) =>
        Assert.False(Months.TryParse(text, Now, out _, out _));
}
