using Flextime;

namespace Test.Flextime;

public class IsoWeeksTests
{
    // A Friday in ISO week 34.
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static (DateOnly From, DateOnly To) Parse(string text)
    {
        Assert.True(IsoWeeks.TryParse(text, Now, out var from, out var to), $"could not read {text}");

        return (from, to);
    }

    [Fact]
    public void AWeekNumberRunsMondayToSunday()
    {
        var (from, to) = Parse("26");

        Assert.Equal(new DateOnly(2026, 6, 22), from);
        Assert.Equal(new DateOnly(2026, 6, 28), to);
        Assert.Equal(DayOfWeek.Monday, from.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, to.DayOfWeek);
    }

    [Fact]
    public void ARangeSpansFromTheFirstMondayToTheLastSunday()
    {
        var (from, to) = Parse("24-26");

        Assert.Equal(new DateOnly(2026, 6, 8), from);
        Assert.Equal(new DateOnly(2026, 6, 28), to);
    }

    [Theory]
    [InlineData("2026-W26")]
    [InlineData("2026W26")]
    [InlineData("2026-w26")]
    public void AnExplicitYearIsAccepted(string text)
    {
        var (from, _) = Parse(text);

        Assert.Equal(new DateOnly(2026, 6, 22), from);
    }

    [Fact]
    public void AnExplicitYearCarriesAcrossARange()
    {
        var (from, to) = Parse("2026-W24-26");

        Assert.Equal(new DateOnly(2026, 6, 8), from);
        Assert.Equal(new DateOnly(2026, 6, 28), to);
    }

    [Fact]
    public void ThisWeekEndsToday()
    {
        // The rest of the week has not happened, and a range running
        // into it would read as though it might hold something.
        var (from, to) = Parse("this");

        Assert.Equal(new DateOnly(2026, 8, 17), from);
        Assert.Equal(new DateOnly(2026, 8, 21), to);
    }

    [Fact]
    public void LastWeekIsWhole()
    {
        var (from, to) = Parse("last");

        Assert.Equal(new DateOnly(2026, 8, 10), from);
        Assert.Equal(new DateOnly(2026, 8, 16), to);
    }

    [Fact]
    public void TheYearComesFromTheWeekNotTheDate()
    {
        // 2027-01-01 is a Friday in ISO week 53 of 2026: a bare week
        // number there means 2026, not 2027.
        var newYear = new DateTimeOffset(2027, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.True(IsoWeeks.TryParse("53", newYear, out var from, out _));
        Assert.Equal(new DateOnly(2026, 12, 28), from);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("54")]
    [InlineData("26-24")]
    [InlineData("next")]
    [InlineData("w/26")]
    [InlineData("2026-W99")]
    public void NonsenseIsRefused(string text) =>
        Assert.False(IsoWeeks.TryParse(text, Now, out _, out _));
}
