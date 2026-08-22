using Flextime.Daemon;

namespace Test.Flextime;

public class ReportTests
{
    private static readonly MachineDataContract[] OneMachine = [new("abc123", "Work laptop")];

    private static ReportDataContract Run(
        ReportedEntry[] reported,
        Dictionary<DateOnly, TimeSpan> measured,
        bool machinesChosen = true,
        IReadOnlySet<DateOnly>? marked = null,
        int threshold = Report.DefaultThreshold) =>
        Report.Reconcile(
            reported,
            measured,
            OneMachine,
            idle: "10",
            profileVersion: null,
            markedDays: marked?.Count ?? 0,
            from: new DateOnly(2026, 6, 1),
            to: new DateOnly(2026, 6, 30),
            machinesChosen,
            marked,
            threshold);

    [Fact]
    public void MeasuredWithNothingReportedIsAFinding()
    {
        // Monday.
        var date = new DateOnly(2026, 6, 22);

        var report = Run([], new Dictionary<DateOnly, TimeSpan> { [date] = TimeSpan.FromHours(7) });

        var finding = Assert.Single(report.Findings);
        Assert.Equal("measured-not-reported", finding.Kind);
        Assert.Equal(date, finding.Date);
    }

    [Fact]
    public void WeekendActivityIsQuiet()
    {
        // Saturday: routinely worked and deliberately not billed, so it
        // must not crowd out the findings.
        var date = new DateOnly(2026, 6, 27);

        var report = Run([], new Dictionary<DateOnly, TimeSpan> { [date] = TimeSpan.FromHours(3) });

        Assert.Empty(report.Findings);
        Assert.Single(report.Quiet);
    }

    [Fact]
    public void UnreportedActivityIsQuietWhenNoMachinesWereChosen()
    {
        // Every computer is in scope by default, personal ones
        // included, so this finding cannot be trusted until the caller
        // says which machines they meant.
        var date = new DateOnly(2026, 6, 22);

        var report = Run(
            [],
            new Dictionary<DateOnly, TimeSpan> { [date] = TimeSpan.FromHours(7) },
            machinesChosen: false);

        Assert.Empty(report.Findings);
        Assert.Single(report.Quiet);
        Assert.False(report.MachinesChosen);
    }

    [Fact]
    public void ActivityOnAMarkedDayIsNotAFinding()
    {
        // The user already said this day was not work; asking them
        // about it again is the tool being deaf, not diligent.
        var date = new DateOnly(2026, 6, 22);

        var report = Run(
            [],
            new Dictionary<DateOnly, TimeSpan> { [date] = TimeSpan.Zero },
            marked: new HashSet<DateOnly> { date });

        Assert.Empty(report.Findings);
        Assert.Single(report.Quiet);
        Assert.Equal(1, report.MarkedDays);
    }

    [Fact]
    public void ReportedWithNothingMeasuredIsAFinding()
    {
        var date = new DateOnly(2026, 6, 22);

        var report = Run([new(date, "ClientA", 90)], []);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("reported-not-measured", finding.Kind);
    }

    [Fact]
    public void ReportedHoursWithNoTagAreAFinding()
    {
        var date = new DateOnly(2026, 6, 7);

        var report = Run(
            [new(date, null, 360)],
            new Dictionary<DateOnly, TimeSpan> { [date] = TimeSpan.FromHours(6) });

        var finding = Assert.Single(report.Findings);
        Assert.Equal("reported-untagged", finding.Kind);
    }

    [Fact]
    public void MatchingDaysProduceNoFindings()
    {
        var date = new DateOnly(2026, 6, 22);

        var report = Run(
            [new(date, "ClientA", 420)],
            new Dictionary<DateOnly, TimeSpan> { [date] = TimeSpan.FromHours(7) });

        Assert.Empty(report.Findings);
    }

    [Fact]
    public void SeveralEntriesOnOneDayCountAsReported()
    {
        // A day split across two clients is reported, even though
        // neither entry alone covers it.
        var date = new DateOnly(2026, 6, 22);

        var report = Run(
            [new(date, "ClientA", 120), new(date, "ClientB", 300)],
            new Dictionary<DateOnly, TimeSpan> { [date] = TimeSpan.FromHours(7) });

        Assert.Empty(report.Findings);
    }

    [Fact]
    public void ADayInsideTheThresholdIsNotADelta()
    {
        // Measured time is not billable time; small differences are
        // the normal state of affairs, not a discrepancy.
        var date = new DateOnly(2026, 6, 22);

        var report = Run(
            [new(date, "ClientA", 440)],
            new Dictionary<DateOnly, TimeSpan> { [date] = TimeSpan.FromHours(7) });

        Assert.Empty(report.Deltas);
    }

    [Fact]
    public void ADayOutsideTheThresholdIsADelta()
    {
        var date = new DateOnly(2026, 6, 22);

        var report = Run(
            [new(date, "ClientA", 300)],
            new Dictionary<DateOnly, TimeSpan> { [date] = TimeSpan.FromHours(7) });

        var delta = Assert.Single(report.Deltas);
        Assert.Equal(300, delta.ReportedMinutes);
        Assert.Equal(420, delta.MeasuredMinutes);
    }

    [Fact]
    public void ADayMissingFromOneSideIsNotAlsoADelta()
    {
        // It is already a categorical finding; saying it twice is
        // noise, and the second telling looks like a different problem.
        var date = new DateOnly(2026, 6, 22);

        var report = Run([], new Dictionary<DateOnly, TimeSpan> { [date] = TimeSpan.FromHours(7) });

        Assert.Single(report.Findings);
        Assert.Empty(report.Deltas);
    }

    [Fact]
    public void MarkedDaysAreLeftOutOfDeltasAndWeeks()
    {
        var date = new DateOnly(2026, 6, 22);

        var report = Run(
            [new(date, "ClientA", 300)],
            new Dictionary<DateOnly, TimeSpan> { [date] = TimeSpan.Zero },
            marked: new HashSet<DateOnly> { date });

        Assert.Empty(report.Deltas);
        Assert.Empty(report.Weeks);
    }

    [Fact]
    public void WeeksSumBothSides()
    {
        var report = Run(
            [
                new(new DateOnly(2026, 6, 22), "ClientA", 420),
                new(new DateOnly(2026, 6, 23), "ClientA", 420),
            ],
            new Dictionary<DateOnly, TimeSpan>
            {
                [new DateOnly(2026, 6, 22)] = TimeSpan.FromHours(7),
                [new DateOnly(2026, 6, 23)] = TimeSpan.FromHours(8),
            });

        var week = Assert.Single(report.Weeks);
        Assert.Equal(26, week.Week);
        Assert.Equal(new DateOnly(2026, 6, 22), week.Monday);
        Assert.Equal(840, week.ReportedMinutes);
        Assert.Equal(900, week.MeasuredMinutes);
    }

    [Fact]
    public void RollupSumsMinutesPerTag()
    {
        var report = Run(
            [
                new(new DateOnly(2026, 6, 22), "ClientA", 120),
                new(new DateOnly(2026, 6, 23), "ClientA", 300),
                new(new DateOnly(2026, 6, 23), "ClientB", 60),
            ],
            []);

        Assert.Equal(2, report.Rollup.Length);
        Assert.Equal("ClientA", report.Rollup[0].Tag);
        Assert.Equal(420, report.Rollup[0].Minutes);
        Assert.Equal(60, report.Rollup[1].Minutes);
    }

    [Fact]
    public void TheMachineSetTravelsWithTheReport()
    {
        var report = Run([], []);

        Assert.Equal(OneMachine[0].Id, report.Machines[0].Id);
        Assert.Equal(OneMachine[0].Name, report.Machines[0].Name);
        Assert.Equal("10", report.Idle);
    }

    [Fact]
    public void ReportedEntriesReadFromJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"flextime-reported-{Guid.NewGuid():N}.json");

        File.WriteAllText(path,
            """[{"date":"2026-06-26","tag":"ClientA","minutes":540},{"date":"2026-06-27","minutes":150}]""");

        try
        {
            var entries = Report.ReadReported(path);

            Assert.Equal(2, entries.Length);
            Assert.Equal(new DateOnly(2026, 6, 26), entries[0].Date);
            Assert.Equal("ClientA", entries[0].Tag);
            Assert.Equal(540, entries[0].Minutes);
            Assert.Null(entries[1].Tag);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
