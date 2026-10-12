using Flextime;
using Google.Protobuf;
using Legacy = Test.Flextime.Legacy;

namespace Test.Flextime;

// Places (field 4) were added to the measurement files.  Old flextime
// and flextimed builds keep reading files written by new ones, and new
// builds with location off write exactly what old ones did.
public class MeasurementFormatTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-format-tests-{Guid.NewGuid():N}");

    public MeasurementFormatTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private static Measurements Sample(bool withPlaces)
    {
        var file = new Measurements { Interval = 60, Zone = "Europe/Stockholm" };

        file.Measurements_.Add(new Measurement { Timestamp = 1760252460, Kind = Measurement.Types.Kind.Start, Idle = 3 });
        file.Measurements_.Add(new Measurement { Timestamp = 1760252520, Kind = Measurement.Types.Kind.Measurement, Idle = 12 });
        file.Measurements_.Add(new Measurement { Timestamp = 1760252580, Kind = Measurement.Types.Kind.Measurement });

        if (withPlaces)
        {
            file.Places.Add(new Place { Timestamp = 1760252460, Router = 0xa1b2c3d4e5f6 });
            file.Places.Add(new Place { Timestamp = 1760252530, Router = 0, Flags = 1 });
            file.Places.Add(new Place { Timestamp = 1760252570, Flags = 4, Hint = "Täby, SE" });
        }

        return file;
    }

    [Fact]
    public void File_with_places_parses_with_the_old_generated_code()
    {
        var bytes = Sample(withPlaces: true).ToByteArray();

        var old = Legacy.Measurements.Parser.ParseFrom(bytes);

        Assert.Equal(60u, old.Interval);
        Assert.Equal("Europe/Stockholm", old.Zone);
        Assert.Equal(
            Sample(withPlaces: false).Measurements_.Select(item => (item.Timestamp, (int)item.Kind, item.Idle)),
            old.Measurements_.Select(item => (item.Timestamp, (int)item.Kind, item.Idle)));
    }

    [Fact]
    public void File_without_places_is_byte_identical_to_the_old_format()
    {
        var old = new Legacy.Measurements { Interval = 60, Zone = "Europe/Stockholm" };

        old.Measurements_.Add(new Legacy.Measurement { Timestamp = 1760252460, Kind = Legacy.Measurement.Types.Kind.Start, Idle = 3 });
        old.Measurements_.Add(new Legacy.Measurement { Timestamp = 1760252520, Kind = Legacy.Measurement.Types.Kind.Measurement, Idle = 12 });
        old.Measurements_.Add(new Legacy.Measurement { Timestamp = 1760252580, Kind = Legacy.Measurement.Types.Kind.Measurement });

        Assert.Equal(old.ToByteArray(), Sample(withPlaces: false).ToByteArray());
    }

    [Fact]
    public void File_without_places_matches_the_golden_bytes()
    {
        // The protobuf encoding of these measurements, worked out by hand:
        // what flextimed 7.12.5 writes.
        const string golden =
            "083c12104575726f70652f53746f636b686f6c6d1a0a08aca4adc706100218031a0a" +
            "08e8a4adc7061001180c1a0808a4a5adc7061001";

        Assert.Equal(golden, Convert.ToHexStringLower(Sample(withPlaces: false).ToByteArray()));
    }

    [Fact]
    public void Reader_reads_the_same_activity_from_a_file_with_places()
    {
        using (var stream = File.Create(Path.Combine(folder, "with.bin")))
        {
            Sample(withPlaces: true).WriteTo(stream);
        }

        var with = Reader.ReadFiles(folder, TimeSpan.MinValue);

        File.Delete(Path.Combine(folder, "with.bin"));

        using (var stream = File.Create(Path.Combine(folder, "without.bin")))
        {
            Sample(withPlaces: false).WriteTo(stream);
        }

        var without = Reader.ReadFiles(folder, TimeSpan.MinValue);

        Assert.Equal(without.Keys, with.Keys);
        Assert.Equal(
            without.Values.SelectMany(day => day.list).Select(item => item.Measurement),
            with.Values.SelectMany(day => day.list).Select(item => item.Measurement));
    }

    [Fact]
    public void Places_round_trip()
    {
        var parsed = Measurements.Parser.ParseFrom(Sample(withPlaces: true).ToByteArray());

        Assert.Equal(Sample(withPlaces: true).Places, parsed.Places);
    }
}
