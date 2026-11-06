using Flextime;

namespace Test.Flextime;

public class MeasurementsFormatterTest
{
    [Theory]
    [ClassData(typeof(MeasurementsData))]
    public void FormatterShouldWork(MeasurementWithZone[] measurements, string expected, TimeSpan idle, int blocksPerDay)
    {
        var formatter = new MeasurementsFormatter(idle, blocksPerDay);
        
        var result = formatter.SummarizeDay(measurements);
        
        Assert.Equal(expected, result);
    }

    private static readonly TimeSpan Stockholm = TimeSpan.FromHours(1);

    private static DateTimeOffset At(int hour, int minute = 0, int second = 0) =>
        new(2023, 12, 1, hour, minute, second, Stockholm);

    private static DateTimeOffset[] Minutes(DateTimeOffset first, int count) =>
        Enumerable.Range(0, count).Select(i => first.AddMinutes(i)).ToArray();

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(60)]
    public void ABlockOfNStampsIsNMinutesOfWork(int count)
    {
        var stamps = Minutes(At(9, 1), count);

        var day = new MeasurementsFormatter(TimeSpan.Zero, 0).ComputeDay(stamps)!;

        Assert.Equal(At(9), day.Start);
        Assert.Equal(stamps[^1], day.End);
        Assert.Equal(TimeSpan.FromMinutes(count), day.Span);
        Assert.Equal(TimeSpan.FromMinutes(count), day.Work);
    }

    [Fact]
    public void OneInactiveMinuteIsAGapAtLimitZero()
    {
        // 09:01–09:05, nothing in the minute ending 09:06, 09:07–09:08.
        DateTimeOffset[] stamps = [.. Minutes(At(9, 1), 5), .. Minutes(At(9, 7), 2)];

        var blocks = MeasurementsFormatter.Blocks(stamps, _ => TimeSpan.Zero);

        Assert.Equal([(At(9), At(9, 5)), (At(9, 6), At(9, 8))], blocks);
        Assert.Equal(TimeSpan.FromMinutes(7), new MeasurementsFormatter(TimeSpan.Zero, 0).ComputeDay(stamps)!.Work);
    }

    [Fact]
    public void OneInactiveMinuteIsSplitAtLimitOneAndBridgedAtTwo()
    {
        DateTimeOffset[] stamps = [.. Minutes(At(9, 1), 5), .. Minutes(At(9, 7), 2)];

        var split = MeasurementsFormatter.Blocks(stamps, _ => TimeSpan.FromMinutes(1));
        var bridged = MeasurementsFormatter.Blocks(stamps, _ => TimeSpan.FromMinutes(2));

        Assert.Equal([(At(9), At(9, 5)), (At(9, 6), At(9, 8))], split);
        Assert.Equal([(At(9), At(9, 8))], bridged);
        Assert.Equal(TimeSpan.FromMinutes(7), new MeasurementsFormatter(TimeSpan.FromMinutes(1), 0).ComputeDay(stamps)!.Work);
        Assert.Equal(TimeSpan.FromMinutes(8), new MeasurementsFormatter(TimeSpan.FromMinutes(2), 0).ComputeDay(stamps)!.Work);
    }

    [Fact]
    public void IdleTimeBelowTheLimitIsBridgedEqualIsNot()
    {
        var formatter = new MeasurementsFormatter(TimeSpan.FromMinutes(10), 0);

        // A 10-minute step is 9 idle minutes, an 11-minute step 10.
        var bridged = formatter.ComputeDay([At(9), At(9, 10)])!;
        var broken = formatter.ComputeDay([At(9), At(9, 11)])!;

        Assert.Equal(TimeSpan.FromMinutes(11), bridged.Work);
        Assert.Equal(TimeSpan.FromMinutes(2), broken.Work);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void IdleTimeCountsInWholeMinutes(int late)
    {
        // A missed minute, the next stamp a second early, on time or
        // late: split at a limit of one minute, bridged at two.
        DateTimeOffset[] stamps = [At(9), At(9, 2).AddSeconds(late)];

        Assert.Equal(2, MeasurementsFormatter.Blocks(stamps, _ => TimeSpan.FromMinutes(1)).Count);
        Assert.Single(MeasurementsFormatter.Blocks(stamps, _ => TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void AFlatLimitBelowOneMinuteCountsAsOne()
    {
        // A stamp off the minute, thirty seconds in, still shares the block.
        DateTimeOffset[] stamps = [At(9), At(9, 0, 30), At(9, 1, 30), At(9, 3)];

        var negative = new MeasurementsFormatter(TimeSpan.FromMinutes(-5), 0).ComputeDay(stamps)!;
        var one = new MeasurementsFormatter(TimeSpan.FromMinutes(1), 0).ComputeDay(stamps)!;

        Assert.Equal(one.Work, negative.Work);
        Assert.Equal(TimeSpan.FromSeconds(3 * 60 + 30), negative.Work);
    }

    [Fact]
    public void TheFirstMinuteStopsAtLocalMidnight()
    {
        // The first stamp is 30 seconds into the day: its leading minute
        // would start on the day before, so it is clipped to 30 seconds.
        // Later blocks keep their whole minute.
        DateTimeOffset[] stamps = [At(0, 0, 30), At(0, 1, 30), At(0, 30)];

        var day = new MeasurementsFormatter(TimeSpan.Zero, 0).ComputeDay(stamps)!;

        Assert.Equal(At(0), day.Start);
        Assert.Equal(new DateOnly(2023, 12, 1), day.Date);
        Assert.Equal(TimeSpan.FromMinutes(30), day.Span);
        Assert.Equal(TimeSpan.FromSeconds(30 + 60 + 60), day.Work);
        Assert.Equal(
            [(At(0), At(0, 1, 30)), (At(0, 29), At(0, 30))],
            MeasurementsFormatter.Blocks(stamps, _ => TimeSpan.Zero));
    }

    private class MeasurementsData : TheoryData<MeasurementWithZone[], string, TimeSpan, int>
    {
        public MeasurementsData()
        {
            MeasurementWithZone Create(DateTimeOffset dateTime, TimeSpan idle) {
                var measurement = new Measurement { Idle = (uint)idle.TotalSeconds, Kind = Measurement.Types.Kind.None, Timestamp = (uint)dateTime.ToUnixTimeSeconds()};

                return new MeasurementWithZone(measurement, "Europe/Stockholm", 60);
            }

            MeasurementWithZone CreateWithZone(DateTimeOffset dateTime, TimeSpan idle, string zone) {
                var measurement = new Measurement { Idle = (uint)idle.TotalSeconds, Kind = Measurement.Types.Kind.None, Timestamp = (uint)dateTime.ToUnixTimeSeconds()};

                return new MeasurementWithZone(measurement, zone, 60);
            }

            Add(Array.Empty<MeasurementWithZone>(), string.Empty, TimeSpan.Zero, 0);
            // A lone measurement still names its day.  This is the shape
            // that used to print as nothing at all, leaving the sync
            // status list with a bare "[in sync]" and no date on it.
            Add(
                [Create(DateTimeOffset.Parse("2026-08-30T10:06:43+02:00"), TimeSpan.Zero)],
                "2026-08-30 10:06 (1 measurement)       w/35 Sun",
                TimeSpan.Zero,
                0);

            // Blocks are a property of a span, so asking for them changes
            // nothing on a day that has none.
            Add(
                [Create(DateTimeOffset.Parse("2026-08-30T10:06:43+02:00"), TimeSpan.Zero)],
                "2026-08-30 10:06 (1 measurement)       w/35 Sun",
                TimeSpan.Zero,
                42);

            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:00+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:09:00+01:00"), TimeSpan.FromMinutes(10))
                ],
                // Each stamp covers the minute ending at it, so the day
                // starts at 06:59 and two stamps 9 minutes apart are 10
                // minutes of work.
                "2023-12-01 06:59 – 07:09 00:10 | 00:10 w/48 Fri",
                TimeSpan.FromMinutes(10),
                0);

            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:12:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10))
                ],
                // Two unbridged stamps are two blocks of one minute each.
                "2023-12-01 07:11 – 08:23 01:12 | 00:02 w/48 Fri",
                TimeSpan.FromMinutes(0),
                0);
            
            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:11:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10))
                ],
                // Ten idle minutes (an 11-minute step less the minute the
                // later stamp covers) against a 10-minute limit is not
                // bridged, matching the web client: three one-minute blocks.
                "2023-12-01 06:59 – 08:23 01:24 | 00:03 w/48 Fri",
                TimeSpan.FromMinutes(10),
                0);

            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:09:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10))
                ],
                "2023-12-01 06:59 – 08:23 01:24 | 00:11 w/48 Fri",
                TimeSpan.FromMinutes(10),
                0);
            
            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:03:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:09:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10)),
                ],
                "2023-12-01 06:59 – 08:23 01:24 | 00:11 w/48 Fri",
                TimeSpan.FromMinutes(10),
                0);
            
            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:12:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10)),
                ],
                "2023-12-01 07:11 – 08:23 01:12 | 00:02 w/48 Fri [07:12/00:01]",
                TimeSpan.FromSeconds(0),
                4);
            
            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:04:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10)),
                ],
                "2023-12-01 06:59 – 08:23 01:24 | 00:03 w/48 Fri [07:04/00:05]",
                TimeSpan.FromMinutes(0),
                1);

            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:03:00+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10)),
                ],
                // 1:59 idle against a 3-minute limit is bridged: 4:59 of
                // work, shown truncated.
                "2023-12-01 06:59 – 08:23 01:24 | 00:04 w/48 Fri [07:03/00:03]",
                TimeSpan.FromMinutes(3),
                2);

            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:03:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:09:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10)),
                ],
                // 2 idle minutes are bridged by a 4-minute limit, 5 are not.
                "2023-12-01 06:59 – 08:23 01:24 | 00:06 w/48 Fri [07:09/00:10, 07:03/00:04]",
                TimeSpan.FromMinutes(4),
                2);
            
            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:00+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:09:00+01:00"), TimeSpan.FromMinutes(10)),
                ],
                "2023-12-01 06:59 – 07:09 00:10 | 00:10 w/48 Fri",
                TimeSpan.FromMinutes(10),
                1);

            Add(
                [
                    CreateWithZone(DateTimeOffset.Parse("2024-02-01T11:18:00+00:00"), TimeSpan.FromMinutes(10), "Europe/London"),
                    CreateWithZone(DateTimeOffset.Parse("2024-02-01T17:17:00-05:00"), TimeSpan.FromMinutes(10), "America/New_York"),
                ],
                // Each time is displayed in its own zone, but durations use the real elapsed time.
                "2024-02-01 11:17 – 17:17 11:00 | 00:02 w/05 Thu [11:18/00:01]",
                TimeSpan.FromMinutes(10),
                1);

            Add(
                [
                    CreateWithZone(DateTimeOffset.Parse("2024-02-01T11:13:00+01:00"), TimeSpan.FromMinutes(10), "Europe/Stockholm"),
                    CreateWithZone(DateTimeOffset.Parse("2024-02-01T11:14:00+00:00"), TimeSpan.FromMinutes(10), "Europe/London"),
                ],
                // Each time is displayed in its own zone, but durations use the real elapsed time.
                "2024-02-01 11:12 – 11:14 01:02 | 00:02 w/05 Thu [11:13/00:01]",
                TimeSpan.FromMinutes(10),
                1);

            Add(
                [
                    Create(DateTimeOffset.Parse("2024-03-31T01:55:00+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2024-03-31T03:05:00+02:00"), TimeSpan.FromMinutes(10)),
                ],
                // Spring forward in Stockholm: the wall clock jumps from 02:00
                // to 03:00, so times display 01:54 – 03:05 but only 11 real
                // minutes pass, and durations use the real elapsed time.
                "2024-03-31 01:54 – 03:05 00:11 | 00:11 w/13 Sun",
                TimeSpan.FromMinutes(10),
                0);

            Add(
                [
                    Create(DateTimeOffset.Parse("2024-10-27T02:30:00+02:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2024-10-27T02:30:00+01:00"), TimeSpan.FromMinutes(10)),
                ],
                // Fall back in Stockholm: 02:30 occurs twice, one real hour
                // apart, so the span is 01:01 and the gap exceeds the idle
                // limit, leaving two one-minute blocks.
                "2024-10-27 02:29 – 02:30 01:01 | 00:02 w/43 Sun",
                TimeSpan.FromMinutes(10),
                0);

            Add(
                [
                    CreateWithZone(DateTimeOffset.Parse("2024-12-31T23:30:00+00:00"), TimeSpan.FromMinutes(10), "Asia/Tokyo"),
                    CreateWithZone(DateTimeOffset.Parse("2024-12-31T23:39:00+00:00"), TimeSpan.FromMinutes(10), "Asia/Tokyo"),
                ],
                // Still December 31 in UTC, but January 1 in Tokyo: the date,
                // ISO week, and weekday follow the measurement zone, not the
                // zone of the machine running this test.
                "2025-01-01 08:29 – 08:39 00:10 | 00:10 w/01 Wed",
                TimeSpan.FromMinutes(10),
                0);

            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:00+01:00"), TimeSpan.FromMinutes(10)),
                ],
                "2023-12-01 07:00 (1 measurement)       w/48 Fri",
                TimeSpan.FromMinutes(10),
                1);
                        
            Add(
                [
                ],
                string.Empty,
                TimeSpan.FromMinutes(10),
                1);
        }
    }
}