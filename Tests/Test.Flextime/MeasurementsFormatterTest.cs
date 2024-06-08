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
                "2023-12-01 07:00 – 08:23 01:23 | 00:00 w/48 Fri",
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
                    CreateWithZone(DateTimeOffset.Parse("2024-02-01T17:17:00-05:00"), TimeSpan.FromMinutes(10), "America/New_York"),
                    CreateWithZone(DateTimeOffset.Parse("2024-02-01T11:18:00+00:00"), TimeSpan.FromMinutes(10), "Europe/London"),
                ],
                "2024-02-01 17:17 – 11:18 05:59 | 05:59 w/05 Thu", // Maybe counter intuitive, but each time should be in local time
                TimeSpan.FromMinutes(10),
                false,
                1);

            Add(
                [
                    CreateWithZone(DateTimeOffset.Parse("2024-02-01T11:13:00+01:00"), TimeSpan.FromMinutes(10), "Europe/Stockholm"),
                    CreateWithZone(DateTimeOffset.Parse("2024-02-01T11:14:00+00:00"), TimeSpan.FromMinutes(10), "Europe/London"),
                ],
                "2024-02-01 11:13 – 11:14 00:01 | 00:01 w/05 Thu", // Maybe counter intuitive, but each time should be in local time
                TimeSpan.FromMinutes(10),
                false,
                1);

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