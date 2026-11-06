using System.Text.Json;
using Flextime;

namespace Test.Flextime;

public class IdleProfileTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static DateTimeOffset At(int hour, int minute = 0, int second = 0) =>
        new(2026, 6, 22, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void FlatProfileAppliesOneLimitAllDay()
    {
        var profile = IdleProfile.Flat(30);

        Assert.Equal(TimeSpan.FromMinutes(30), profile.LimitAt(TimeSpan.FromHours(3)));
        Assert.Equal(TimeSpan.FromMinutes(30), profile.LimitAt(TimeSpan.FromHours(23)));
    }

    [Fact]
    public void RawBinArraysAreAccepted()
    {
        // The shape older builds wrote.
        var profile = IdleProfile.Decode(Json("[5, 45]"));

        Assert.Equal(TimeSpan.FromMinutes(5), profile.LimitAt(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMinutes(45), profile.LimitAt(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void RunLengthPairsAreAccepted()
    {
        var profile = IdleProfile.Decode(Json("[[60, 45], [60, 1]]"));

        Assert.Equal(TimeSpan.FromMinutes(45), profile.LimitAt(TimeSpan.FromMinutes(59)));
        Assert.Equal(TimeSpan.FromMinutes(1), profile.LimitAt(TimeSpan.FromMinutes(60)));
    }

    [Fact]
    public void ShortCurvesArePaddedToAWholeDay()
    {
        var profile = IdleProfile.Decode(Json("[[10, 45]]"));

        Assert.Equal(TimeSpan.FromMinutes(10), profile.LimitAt(TimeSpan.FromHours(23)));
    }

    [Fact]
    public void AnEmptyCurveMeansThePlainDefault()
    {
        var profile = IdleProfile.Decode(Json("[]"));

        Assert.Equal(TimeSpan.FromMinutes(10), profile.LimitAt(TimeSpan.FromHours(9)));
    }

    [Fact]
    public void LimitsAreClampedAndRoundedHalfUp()
    {
        // JavaScript's Math.round is half-up; .NET rounds to even by
        // default, which would put this bin a minute lower.
        var profile = IdleProfile.Decode(Json("[2.5, 0, 100000]"));

        Assert.Equal(TimeSpan.FromMinutes(3), profile.LimitAt(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMinutes(1), profile.LimitAt(TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromMinutes(24 * 60), profile.LimitAt(TimeSpan.FromMinutes(2)));
    }

    [Theory]
    [InlineData("\"flat\"")]
    [InlineData("[[0, 10]]")]
    [InlineData("[[1]]")]
    [InlineData("[[\"a\", \"b\"]]")]
    public void MalformedCurvesAreRefused(string stored) =>
        Assert.Throws<IdlePolicyException>(() => IdleProfile.Decode(Json(stored)));

    [Fact]
    public void AGapIsBridgedWhenItEqualsTheLimit()
    {
        // Inclusive, matching the web client.
        var profile = IdleProfile.Flat(10);

        var day = MeasurementsFormatter.ComputeDay([At(9), At(9, 10)], profile);

        Assert.Equal(TimeSpan.FromMinutes(10), day!.Work);
    }

    [Fact]
    public void TheLimitIsTheOneWhereTheGapStarts()
    {
        // Generous until 17:00, strict after.  A gap opening at 16:55
        // is bridged by the generous limit even though it ends well
        // inside the strict one.
        var profile = IdleProfile.Decode(Json("[[1020, 60], [420, 1]]"));

        var bridged = MeasurementsFormatter.ComputeDay([At(16, 55), At(17, 30)], profile);
        var broken = MeasurementsFormatter.ComputeDay([At(17, 5), At(17, 40)], profile);

        Assert.Equal(TimeSpan.FromMinutes(35), bridged!.Work);
        Assert.Equal(TimeSpan.Zero, broken!.Work);
    }

    [Fact]
    public void TheLastAppliedProfileClaimingTheDayWins()
    {
        var policy = IdlePolicy.Decode(Json("""
            {
              "shared": [[1440, 10]],
              "library": [
                { "id": "a", "name": "Office", "days": ["mon"], "profile": [[1440, 30]] },
                { "id": "b", "name": "Other",  "days": ["mon"], "profile": [[1440, 45]] }
              ],
              "applied": ["a", "b"]
            }
            """));

        var monday = new DateOnly(2026, 6, 22);

        Assert.Equal(TimeSpan.FromMinutes(45), policy.For(monday, DayOfWeek.Monday).LimitAt(TimeSpan.FromHours(9)));
    }

    [Fact]
    public void ADayNobodyClaimsFallsBackToTheDefault()
    {
        var policy = IdlePolicy.Decode(Json("""
            {
              "shared": [[1440, 10]],
              "library": [{ "id": "a", "name": "Office", "days": ["mon"], "profile": [[1440, 30]] }],
              "applied": ["a"]
            }
            """));

        Assert.Equal(
            TimeSpan.FromMinutes(10),
            policy.For(new DateOnly(2026, 6, 23), DayOfWeek.Tuesday).LimitAt(TimeSpan.FromHours(9)));
    }

    [Fact]
    public void AnOverrideBeatsEverything()
    {
        var policy = IdlePolicy.Decode(Json("""
            {
              "shared": [[1440, 10]],
              "library": [{ "id": "a", "name": "Office", "days": ["mon"], "profile": [[1440, 30]] }],
              "applied": ["a"],
              "overrides": { "2026-06-22": [[1440, 5]] }
            }
            """));

        var monday = new DateOnly(2026, 6, 22);

        Assert.Equal(TimeSpan.FromMinutes(5), policy.For(monday, DayOfWeek.Monday).LimitAt(TimeSpan.FromHours(9)));
    }

    [Fact]
    public void AnAppliedIdNoLongerInTheLibraryIsIgnored()
    {
        var policy = IdlePolicy.Decode(Json("""
            { "shared": [[1440, 10]], "library": [], "applied": ["deleted"] }
            """));

        Assert.Empty(policy.Applied);
    }

    [Fact]
    public void ADocumentWithNoLibraryUsesTheDefaultCurve()
    {
        // The web seeds a library here; numerically that reduces to the
        // default curve, so seeding need not be reimplemented.
        var policy = IdlePolicy.Decode(Json("""{ "shared": [[1440, 25]] }"""));

        Assert.Equal(
            TimeSpan.FromMinutes(25),
            policy.For(new DateOnly(2026, 6, 22), DayOfWeek.Monday).LimitAt(TimeSpan.FromHours(9)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "library": "no" }""")]
    [InlineData("""{ "library": [{ "name": "x", "days": [] }] }""")]
    [InlineData("""{ "library": [{ "id": "a", "name": "x", "days": ["funday"] }] }""")]
    [InlineData("""{ "overrides": { "not-a-date": [] } }""")]
    public void MalformedPolicyDocumentsAreRefused(string stored) =>
        Assert.Throws<IdlePolicyException>(() => IdlePolicy.Decode(Json(stored)));

    [Fact]
    public void WholeDayMarksAreRead()
    {
        var marks = Marks.Decode(Json("""{ "marks": { "2026-06-22": "day" } }"""));

        Assert.True(marks.IsMarked(new DateOnly(2026, 6, 22)));
        Assert.False(marks.IsMarked(new DateOnly(2026, 6, 23)));
    }

    [Fact]
    public void RangeMarksAreRefused()
    {
        // A shape the stored format allows and the web decodes, but
        // nothing subtracts yet.  Accepting it would mean counting the
        // day differently from the client that wrote it.
        var exception = Assert.Throws<IdlePolicyException>(
            () => Marks.Decode(Json("""{ "marks": { "2026-06-22": [{ "from": 0, "to": 3600 }] } }""")));

        Assert.Contains("ranges", exception.Message);
    }

    [Fact]
    public void AnAbsentMarksDocumentIsEmpty()
    {
        Assert.Empty(Marks.Decode(Json("{}")).Days);
    }
}
