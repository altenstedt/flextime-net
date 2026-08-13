using Flextime;

namespace Test.Flextime;

public class MeasurementsFormatterTest
{
    [Theory]
    [ClassData(typeof(MeasurementsData))]
    public void FormatterShouldWork(MeasurementWithZone[] measurements, string expected, TimeSpan idle, bool verbose, int blocksPerDay)
    {
        var formatter = new MeasurementsFormatter(idle, verbose, blocksPerDay);
        
        var result = formatter.SummarizeDay(measurements);
        
        Assert.Equal(expected, result);
    }

    private class MeasurementsData : TheoryData<MeasurementWithZone[], string, TimeSpan, bool, int>
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

            Add(Array.Empty<MeasurementWithZone>(), string.Empty, TimeSpan.Zero, false, 0);
            Add([Create(DateTimeOffset.Now, TimeSpan.Zero)], string.Empty, TimeSpan.Zero, false, 0); // Single measurement
            Add([Create(DateTimeOffset.Now, TimeSpan.Zero)], string.Empty, TimeSpan.Zero, false, 42); // Single measurement

            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:00+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:09:00+01:00"), TimeSpan.FromMinutes(10))
                ],
                "2023-12-01 07:00 – 07:09 00:09 | 00:09 w/48 Fri",
                TimeSpan.FromMinutes(10),
                false,
                0);

            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:12:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10))
                ],
                "2023-12-01 07:12 – 08:23 01:11 | 00:00 w/48 Fri",
                TimeSpan.FromMinutes(0),
                false,
                0);
            
            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:10:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10))
                ],
                // A gap of exactly the idle limit counts as active —
                // inclusive, matching the web client.
                "2023-12-01 07:00 – 08:23 01:23 | 00:10 w/48 Fri",
                TimeSpan.FromMinutes(10),
                false,
                0);

            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:09:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10))
                ],
                "2023-12-01 07:00 – 08:23 01:23 | 00:09 w/48 Fri",
                TimeSpan.FromMinutes(10),
                false,
                0);
            
            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:03:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:09:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10)),
                ],
                "2023-12-01 07:00 – 08:23 01:23 | 00:09 w/48 Fri",
                TimeSpan.FromMinutes(10),
                false,
                0);
            
            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:12:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10)),
                ],
                "2023-12-01 07:12 – 08:23 01:11 | 00:00 w/48 Fri [07:12/00:00]",
                TimeSpan.FromSeconds(0),
                false,
                4);
            
            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:04:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10)),
                ],
                "2023-12-01 07:00 – 08:23 01:23 | 00:00 w/48 Fri [07:04/00:04]",
                TimeSpan.FromMinutes(0),
                false,
                1);

            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:03:00+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10)),
                ],
                "2023-12-01 07:00 – 08:23 01:23 | 00:02 w/48 Fri [07:03/00:02]",
                TimeSpan.FromMinutes(3),
                false,
                2);

            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:03:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:09:01+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T08:23:49+01:00"), TimeSpan.FromMinutes(10)),
                ],
                "2023-12-01 07:00 – 08:23 01:23 | 00:03 w/48 Fri [07:09/00:09, 07:03/00:03]",
                TimeSpan.FromMinutes(4),
                false,
                2);
            
            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:00+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2023-12-01T07:09:00+01:00"), TimeSpan.FromMinutes(10)),
                ],
                "2023-12-01 07:00 – 07:09 00:09 | 00:09 w/48 Fri",
                TimeSpan.FromMinutes(10),
                false,
                1);

            Add(
                [
                    CreateWithZone(DateTimeOffset.Parse("2024-02-01T11:18:00+00:00"), TimeSpan.FromMinutes(10), "Europe/London"),
                    CreateWithZone(DateTimeOffset.Parse("2024-02-01T17:17:00-05:00"), TimeSpan.FromMinutes(10), "America/New_York"),
                ],
                // Each time is displayed in its own zone, but durations use the real elapsed time.
                "2024-02-01 11:18 – 17:17 10:59 | 00:00 w/05 Thu [11:18/00:00]",
                TimeSpan.FromMinutes(10),
                false,
                1);

            Add(
                [
                    CreateWithZone(DateTimeOffset.Parse("2024-02-01T11:13:00+01:00"), TimeSpan.FromMinutes(10), "Europe/Stockholm"),
                    CreateWithZone(DateTimeOffset.Parse("2024-02-01T11:14:00+00:00"), TimeSpan.FromMinutes(10), "Europe/London"),
                ],
                // Each time is displayed in its own zone, but durations use the real elapsed time.
                "2024-02-01 11:13 – 11:14 01:01 | 00:00 w/05 Thu [11:13/00:00]",
                TimeSpan.FromMinutes(10),
                false,
                1);

            Add(
                [
                    Create(DateTimeOffset.Parse("2024-03-31T01:55:00+01:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2024-03-31T03:05:00+02:00"), TimeSpan.FromMinutes(10)),
                ],
                // Spring forward in Stockholm: the wall clock jumps from 02:00
                // to 03:00, so times display 01:55 – 03:05 but only 10 real
                // minutes pass, and durations use the real elapsed time.
                "2024-03-31 01:55 – 03:05 00:10 | 00:10 w/13 Sun",
                TimeSpan.FromMinutes(10),
                false,
                0);

            Add(
                [
                    Create(DateTimeOffset.Parse("2024-10-27T02:30:00+02:00"), TimeSpan.FromMinutes(10)),
                    Create(DateTimeOffset.Parse("2024-10-27T02:30:00+01:00"), TimeSpan.FromMinutes(10)),
                ],
                // Fall back in Stockholm: 02:30 occurs twice, one real hour
                // apart, so the span is 01:00 and the gap exceeds the idle
                // limit.
                "2024-10-27 02:30 – 02:30 01:00 | 00:00 w/43 Sun",
                TimeSpan.FromMinutes(10),
                false,
                0);

            Add(
                [
                    CreateWithZone(DateTimeOffset.Parse("2024-12-31T23:30:00+00:00"), TimeSpan.FromMinutes(10), "Asia/Tokyo"),
                    CreateWithZone(DateTimeOffset.Parse("2024-12-31T23:39:00+00:00"), TimeSpan.FromMinutes(10), "Asia/Tokyo"),
                ],
                // Still December 31 in UTC, but January 1 in Tokyo: the date,
                // ISO week, and weekday follow the measurement zone, not the
                // zone of the machine running this test.
                "2025-01-01 08:30 – 08:39 00:09 | 00:09 w/01 Wed",
                TimeSpan.FromMinutes(10),
                false,
                0);

            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:00+01:00"), TimeSpan.FromMinutes(10)),
                ],
                string.Empty,
                TimeSpan.FromMinutes(10),
                false,
                1);
            
            Add(
                [
                    Create(DateTimeOffset.Parse("2023-12-01T07:00:00+01:00"), TimeSpan.FromMinutes(10)),
                ],
                "Single measurement",
                TimeSpan.FromMinutes(10),
                true,
                1);
            
            Add(
                [
                ],
                string.Empty,
                TimeSpan.FromMinutes(10),
                false,
                1);
        }
    }
}