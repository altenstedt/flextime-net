// See https://aka.ms/new-console-template for more information

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Flextime;

BenchmarkRunner.Run<FormatterBenchmark>();

[MemoryDiagnoser]
public class FormatterBenchmark
{
    private readonly MeasurementsFormatter formatter = new(TimeSpan.FromMinutes(10), false, 0);
    private readonly MeasurementWithZone[] measurementWithZones;

    public FormatterBenchmark()
    {
        var now = DateTimeOffset.UtcNow;

        // 12 hours work every minute
        measurementWithZones = Enumerable.Range(0, 12 * 60)
            .Select(i => new MeasurementWithZone(new Measurement { Timestamp = (uint)now.AddMinutes(i).ToUnixTimeSeconds()}, "Europe/Stockholm", 60))
            .ToArray();
    }
    
    [Benchmark]
    public void PrintOneWorkDay()
    {
        formatter.SummarizeDay(measurementWithZones);
    }
}