using System.Diagnostics;
using System.Text;
using Flextime;
using Flextime.Daemon;
using Flextime.Monitor;
using Microsoft.Extensions.Logging.Abstractions;

namespace Test.Flextime;

// The pause file, as listen reads it.  Listen must keep measuring
// whatever is at the path, so every way the file can be wrong is tried
// here, through a fake file system and the real one.
public class PauseTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-pause-tests-{Guid.NewGuid():N}");

    // 2026-11-16 19:44:30 in Stockholm.
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 18, 44, 30, TimeSpan.Zero);

    private static readonly TimeZoneInfo Stockholm = TimeZones.Get("Europe/Stockholm");

    public PauseTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Undo the permission tests, or the folder cannot be removed.
            foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Prepend(folder))
            {
                try
                {
                    if (new FileInfo(path).LinkTarget == null)
                    {
                        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        Directory.Delete(folder, recursive: true);
    }

    private string PausePath => Path.Combine(folder, "pause.txt");

    private static string Text(DateTimeOffset end, DateTimeOffset written) => PauseFile.Format(end, written);

    // ---- The text -------------------------------------------------------

    [Fact]
    public void A_pause_written_now_is_in_force_until_its_end()
    {
        var status = PauseFile.Parse(Text(Now.AddHours(4), Now), Now);

        Assert.Equal(PauseState.Paused, status.State);
        Assert.Equal(Now.AddHours(4).ToUnixTimeSeconds(), status.End!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void A_pause_whose_end_has_passed_measures()
    {
        // Slept through the end, or the clock jumped forward.
        Assert.Equal(PauseState.Ended, PauseFile.Parse(Text(Now.AddHours(4), Now), Now.AddHours(4)).State);
        Assert.Equal(PauseState.Ended, PauseFile.Parse(Text(Now.AddHours(4), Now), Now.AddDays(30)).State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1763333070")]
    [InlineData("garbage\nmore garbage")]
    [InlineData("-1763333070\n1763318670")]
    [InlineData("+1763333070\n1763318670")]
    [InlineData("1763 333070\n1763318670")]
    [InlineData("0x1F\n1763318670")]
    [InlineData("99999999999999999999999\n1763318670")]
    [InlineData("1763333070.5\n1763318670")]
    [InlineData("\0\0\0\0\n\0\0")]
    [InlineData("１７６３３３３０７０\n1763318670")]
    public void Anything_but_two_instants_is_no_pause(string text)
    {
        var status = PauseFile.Parse(text, Now);

        Assert.Equal(PauseState.Invalid, status.State);
        Assert.False(status.IsPaused);
    }

    [Fact]
    public void An_end_in_the_far_future_is_no_pause()
    {
        // Year 9999, and past what DateTimeOffset holds.
        Assert.False(PauseFile.Parse($"253402300799\n{Now.ToUnixTimeSeconds()}", Now).IsPaused);
        Assert.False(PauseFile.Parse($"253402300800\n{Now.ToUnixTimeSeconds()}", Now).IsPaused);
        Assert.False(PauseFile.Parse($"{long.MaxValue}\n{Now.ToUnixTimeSeconds()}", Now).IsPaused);
    }

    [Fact]
    public void A_pause_longer_than_the_end_of_the_next_day_is_no_pause()
    {
        Assert.True(PauseFile.Parse(Text(Now + PauseFile.MaxLength, Now), Now).IsPaused);
        Assert.False(PauseFile.Parse(Text(Now + PauseFile.MaxLength + TimeSpan.FromSeconds(1), Now), Now).IsPaused);
    }

    [Fact]
    public void A_pause_written_after_now_is_no_pause()
    {
        // The clock moved back after the pause was written: the pause
        // could otherwise last a long time beyond what it said.
        var written = Now.AddHours(3);

        Assert.Equal(PauseState.Invalid, PauseFile.Parse(Text(written.AddHours(1), written), Now).State);

        // A little skew is fine.
        Assert.True(PauseFile.Parse(Text(Now.AddHours(1), Now.AddMinutes(1)), Now).IsPaused);
    }

    [Fact]
    public void An_end_before_it_was_written_is_no_pause()
    {
        Assert.Equal(PauseState.Invalid, PauseFile.Parse(Text(Now.AddMinutes(-1), Now), Now).State);
        Assert.Equal(PauseState.Invalid, PauseFile.Parse(Text(Now, Now), Now).State);
    }

    [Fact]
    public void Windows_line_ends_and_the_comment_line_are_read()
    {
        var text = Text(Now.AddHours(1), Now).Replace("\n", "\r\n");

        Assert.True(PauseFile.Parse(text, Now).IsPaused);
    }

    // ---- The times the command computes ----------------------------------

    [Fact]
    public void The_default_end_is_the_coming_midnight()
    {
        var end = PauseFile.StartOfDay(Now, 1, Stockholm);

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.FromHours(1)), end);
        Assert.Equal("00:00", PauseFile.Describe(end, Now, Stockholm));
    }

    [Fact]
    public void Until_a_time_that_has_passed_today_means_tomorrow()
    {
        var end = PauseFile.Next(Now, new TimeOnly(7, 0), Stockholm);

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 7, 0, 0, TimeSpan.FromHours(1)), end);
        Assert.Equal("Tue 2026-11-17 07:00", PauseFile.Describe(end, Now, Stockholm));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 22, 0, 0, TimeSpan.FromHours(1)),
            PauseFile.Next(Now, new TimeOnly(22, 0), Stockholm));
    }

    [Fact]
    public void The_latest_end_lies_within_what_listen_accepts_on_a_daylight_saving_night()
    {
        // The night the clocks go back has 25 hours.
        var evening = new DateTimeOffset(2026, 10, 24, 23, 59, 0, TimeSpan.FromHours(2));
        var latest = PauseFile.Latest(evening, Stockholm);

        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.FromHours(1)), latest);
        Assert.True(latest - evening <= PauseFile.MaxLength);
        Assert.True(PauseFile.Parse(Text(latest, evening), evening).IsPaused);
    }

    [Fact]
    public void A_skipped_midnight_becomes_the_first_minute_after_it()
    {
        // Santiago moves its clocks forward at midnight.
        var santiago = TimeZones.Get("America/Santiago");
        var evening = new DateTimeOffset(2026, 9, 5, 22, 0, 0, TimeSpan.FromHours(-4));
        var end = PauseFile.StartOfDay(evening, 1, santiago);

        Assert.False(santiago.IsInvalidTime(end.DateTime));
        Assert.True(end > evening);
        Assert.True(end - evening < TimeSpan.FromHours(4));
    }

    // ---- The reader, over a fake file system -----------------------------

    private sealed class FakeSource : IPauseFileSource
    {
        public Func<PauseFileStat?> OnStat { get; set; } = () => null;
        public Func<byte[]> OnRead { get; set; } = () => [];
        public int Reads { get; private set; }

        public PauseFileStat? Stat() => OnStat();

        public byte[] Read(int maxBytes)
        {
            Reads++;

            return OnRead();
        }
    }

    private static PauseFileStat FileOf(int length, int version = 0) =>
        new(false, false, length, new DateTime(2026, 11, 16, 18, 44, version, DateTimeKind.Utc));

    private static FakeSource Holding(string text, int version = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        return new FakeSource { OnStat = () => FileOf(bytes.Length, version), OnRead = () => bytes };
    }

    [Fact]
    public void No_file_is_no_pause_and_nothing_logged()
    {
        var warnings = new List<string>();
        var status = new PauseReader(new FakeSource(), warnings.Add).Check(Now);

        Assert.Equal(PauseState.None, status.State);
        Assert.Empty(warnings);
    }

    [Fact]
    public void A_valid_file_pauses()
    {
        Assert.True(new PauseReader(Holding(Text(Now.AddHours(1), Now))).Check(Now).IsPaused);
    }

    public static TheoryData<string, PauseFileStat?, string?> BadFiles => new()
    {
        { "a directory", new PauseFileStat(true, false, 0, default), null },
        { "a symbolic link", new PauseFileStat(false, true, 0, default), null },
        { "empty, or a pipe", new PauseFileStat(false, false, 0, default), null },
        { "huge", new PauseFileStat(false, false, 10_000_000, default), null },
        { "truncated", null, "17633" },
        { "garbage", null, "this is not a pause\nat all" },
        { "invalid UTF-8", null, "��\n�" },
    };

    [Theory]
    [MemberData(nameof(BadFiles))]
    public void A_bad_file_measures_and_is_logged_once(string what, PauseFileStat? stat, string? text)
    {
        _ = what;

        var warnings = new List<string>();
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        var source = new FakeSource
        {
            OnStat = () => stat ?? FileOf(bytes.Length),
            OnRead = () => bytes,
        };
        var reader = new PauseReader(source, warnings.Add);

        for (var tick = 0; tick < 10; tick++)
        {
            Assert.False(reader.Check(Now.AddMinutes(tick)).IsPaused);
        }

        Assert.Single(warnings);
        Assert.StartsWith("Ignoring the pause file and measuring", warnings[0]);

        // Never opened: a link could lead anywhere, a pipe would block.
        if (stat != null)
        {
            Assert.Equal(0, source.Reads);
        }
    }

    [Fact]
    public void A_file_larger_than_it_said_measures()
    {
        // It grew between the look and the read.
        var source = new FakeSource
        {
            OnStat = () => FileOf(20),
            OnRead = () => new byte[PauseFile.MaxBytes + 1],
        };

        Assert.Equal(PauseState.Invalid, new PauseReader(source).Check(Now).State);
    }

    [Theory]
    [InlineData("stat")]
    [InlineData("read")]
    public void A_file_that_cannot_be_read_measures_and_is_logged_once(string where)
    {
        var warnings = new List<string>();
        var bytes = Encoding.UTF8.GetBytes(Text(Now.AddHours(1), Now));
        var source = new FakeSource
        {
            OnStat = () => where == "stat" ? throw new UnauthorizedAccessException("denied") : FileOf(bytes.Length),
            OnRead = () => throw new IOException("The process cannot access the file because it is being used by another process."),
        };
        var reader = new PauseReader(source, warnings.Add);

        for (var tick = 0; tick < 5; tick++)
        {
            Assert.False(reader.Check(Now).IsPaused);
        }

        Assert.Single(warnings);
    }

    [Fact]
    public void Any_exception_measures()
    {
        var source = new FakeSource { OnStat = () => throw new InvalidOperationException("anything") };

        Assert.Equal(PauseState.Invalid, new PauseReader(source).Check(Now).State);
    }

    [Fact]
    public void A_logger_that_throws_does_not_stop_measuring()
    {
        var reader = new PauseReader(Holding("garbage\n"), _ => throw new InvalidOperationException("log is full"));

        Assert.Equal(PauseState.Invalid, reader.Check(Now).State);
    }

    [Fact]
    public void An_unchanged_file_is_read_once_and_still_ends_on_time()
    {
        var source = Holding(Text(Now.AddMinutes(3), Now));
        var reader = new PauseReader(source);

        Assert.True(reader.Check(Now).IsPaused);
        Assert.True(reader.Check(Now.AddMinutes(1)).IsPaused);
        Assert.True(reader.Check(Now.AddMinutes(2)).IsPaused);
        Assert.Equal(PauseState.Ended, reader.Check(Now.AddMinutes(3)).State);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public void A_changed_file_is_read_again()
    {
        var text = Text(Now.AddMinutes(3), Now);
        var version = 0;
        var source = new FakeSource
        {
            OnStat = () => FileOf(Encoding.UTF8.GetByteCount(text), version),
            OnRead = () => Encoding.UTF8.GetBytes(text),
        };
        var reader = new PauseReader(source);

        Assert.True(reader.Check(Now).IsPaused);

        text = Text(Now.AddMinutes(2), Now);
        version = 1;

        Assert.Equal(PauseState.Ended, reader.Check(Now.AddMinutes(2)).State);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void A_problem_is_logged_again_after_the_file_was_fine()
    {
        var warnings = new List<string>();
        var good = true;
        var goodBytes = Encoding.UTF8.GetBytes(Text(Now.AddHours(1), Now));
        var source = new FakeSource
        {
            OnStat = () => good ? FileOf(goodBytes.Length, 1) : FileOf(3, 2),
            OnRead = () => good ? goodBytes : "bad"u8.ToArray(),
        };
        var reader = new PauseReader(source, warnings.Add);

        foreach (var state in new[] { false, false, true, true, false, false })
        {
            good = state;
            reader.Check(Now);
        }

        Assert.Equal(2, warnings.Count);
    }

    // ---- The reader, over the real file system --------------------------

    [Fact]
    public void A_written_pause_is_read_and_leaves_no_temporary_file()
    {
        PauseFile.Write(PausePath, Now.AddHours(1), Now);

        var status = new PauseReader(new PauseFileSource(PausePath)).Check(Now);

        Assert.True(status.IsPaused);
        Assert.Equal(new[] { "pause.txt" }, Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public void A_rewrite_replaces_a_link_rather_than_writing_through_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Path.Combine(folder, "elsewhere.txt");

        File.WriteAllText(target, "keep");
        File.CreateSymbolicLink(PausePath, target);

        PauseFile.Write(PausePath, Now.AddHours(1), Now);

        Assert.Equal("keep", File.ReadAllText(target));
        Assert.True(new PauseReader(new PauseFileSource(PausePath)).Check(Now).IsPaused);
    }

    [Fact]
    public void A_directory_at_the_path_measures()
    {
        Directory.CreateDirectory(PausePath);

        Assert.Equal(PauseState.Invalid, new PauseReader(new PauseFileSource(PausePath)).Check(Now).State);
    }

    [Fact]
    public void A_link_to_a_valid_pause_measures()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Path.Combine(folder, "elsewhere.txt");

        File.WriteAllText(target, Text(Now.AddHours(1), Now));
        File.CreateSymbolicLink(PausePath, target);

        Assert.Equal(PauseState.Invalid, new PauseReader(new PauseFileSource(PausePath)).Check(Now).State);
    }

    [Fact]
    public void A_dangling_link_measures()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.CreateSymbolicLink(PausePath, Path.Combine(folder, "nowhere.txt"));

        Assert.Equal(PauseState.Invalid, new PauseReader(new PauseFileSource(PausePath)).Check(Now).State);
    }

    [Fact]
    public void A_huge_file_measures()
    {
        File.WriteAllBytes(PausePath, new byte[1024 * 1024]);

        Assert.Equal(PauseState.Invalid, new PauseReader(new PauseFileSource(PausePath)).Check(Now).State);
    }

    [Fact]
    public void An_unreadable_file_measures()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        PauseFile.Write(PausePath, Now.AddHours(1), Now);
        File.SetUnixFileMode(PausePath, UnixFileMode.None);

        Assert.Equal(PauseState.Invalid, new PauseReader(new PauseFileSource(PausePath)).Check(Now).State);
    }

    [Fact]
    public async Task A_pipe_at_the_path_is_never_opened()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/mkfifo"))
        {
            return;
        }

        using (var mkfifo = Process.Start("/usr/bin/mkfifo", [PausePath]))
        {
            await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        // Opening a pipe for reading blocks until a writer comes; the
        // check must not.
        var check = Task.Run(() => new PauseReader(new PauseFileSource(PausePath)).Check(Now), TestContext.Current.CancellationToken);
        var done = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Same(check, done);
        Assert.False((await check).IsPaused);
    }

    [Fact]
    public async Task A_file_being_replaced_never_reads_half_written()
    {
        // The writer replaces the file in one rename, so every read sees
        // one whole pause or the other.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        PauseFile.Write(PausePath, Now.AddHours(1), Now);

        var writer = Task.Run(() =>
        {
            var hours = 1;

            while (!stop.IsCancellationRequested)
            {
                PauseFile.Write(PausePath, Now.AddHours(hours % 5 + 1), Now);
                hours++;
            }
        }, TestContext.Current.CancellationToken);

        var states = new List<PauseState>();

        while (!stop.IsCancellationRequested)
        {
            states.Add(new PauseReader(new PauseFileSource(PausePath)).Check(Now).State);
        }

        await writer;

        // On Windows a read can lose to the rename and fail; that reads
        // as no pause, which is the safe side.  Nowhere does a read see
        // a half file and take it as a different pause.
        Assert.All(states, state => Assert.True(state is PauseState.Paused or PauseState.Invalid or PauseState.None));

        if (!OperatingSystem.IsWindows())
        {
            Assert.All(states, state => Assert.Equal(PauseState.Paused, state));
        }
    }

    // ---- Clearing -------------------------------------------------------

    [Fact]
    public void Clearing_nothing_succeeds()
    {
        Assert.Null(PauseFile.Clear(PausePath));
    }

    [Fact]
    public void Clearing_removes_the_pause()
    {
        PauseFile.Write(PausePath, Now.AddHours(1), Now);

        Assert.Null(PauseFile.Clear(PausePath));
        Assert.False(File.Exists(PausePath));
    }

    [Fact]
    public void Clearing_a_link_removes_the_link_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Path.Combine(folder, "elsewhere.txt");

        File.WriteAllText(target, "keep");
        File.CreateSymbolicLink(PausePath, target);

        Assert.Null(PauseFile.Clear(PausePath));
        Assert.Equal("keep", File.ReadAllText(target));
    }

    [Fact]
    public void Clearing_a_directory_leaves_it_but_measures()
    {
        Directory.CreateDirectory(PausePath);

        Assert.Null(PauseFile.Clear(PausePath));
    }

    [Fact]
    public void A_pause_that_cannot_be_removed_is_emptied()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var locked = Path.Combine(folder, "locked");
        var path = Path.Combine(locked, "pause.txt");

        Directory.CreateDirectory(locked);
        PauseFile.Write(path, Now.AddHours(1), Now);

        // The file stays writable, but the folder no longer lets it go.
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.Null(PauseFile.Clear(path));
        Assert.False(new PauseReader(new PauseFileSource(path)).Check(DateTimeOffset.UtcNow).IsPaused);
    }

    [Fact]
    public void A_pause_that_cannot_be_removed_or_emptied_is_reported()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var locked = Path.Combine(folder, "locked");
        var path = Path.Combine(locked, "pause.txt");
        var now = DateTimeOffset.UtcNow;

        Directory.CreateDirectory(locked);
        PauseFile.Write(path, now.AddHours(1), now);
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.NotNull(PauseFile.Clear(path));
    }

    // ---- What listen does with it ---------------------------------------

    [Fact]
    public void Stop_and_start_are_written_once_per_change_however_the_file_flaps()
    {
        var gate = new PauseGate();
        var steps = new[] { false, true, true, false, true, false, false, true, true, true, false }
            .Select(gate.Next)
            .ToList();

        Assert.Equal(new[]
        {
            PauseStep.Measure, PauseStep.Enter, PauseStep.Skip, PauseStep.Leave, PauseStep.Enter,
            PauseStep.Leave, PauseStep.Measure, PauseStep.Enter, PauseStep.Skip, PauseStep.Skip, PauseStep.Leave,
        }, steps);
    }

    [Fact]
    public void A_pause_reader_that_throws_measures()
    {
        var monitor = new UserInputMonitor(NullLogger<UserInputMonitor>.Instance, new UserInputMonitorOptions
        {
            PausedUntil = _ => throw new IOException("anything"),
        });

        Assert.Null(monitor.PausedUntilAt(Now));
    }

    [Fact]
    public void A_pause_that_has_ended_measures_whatever_the_reader_says()
    {
        var monitor = new UserInputMonitor(NullLogger<UserInputMonitor>.Instance, new UserInputMonitorOptions
        {
            PausedUntil = now => now.AddMinutes(-1),
        });

        Assert.Null(monitor.PausedUntilAt(Now));
    }

    [Fact]
    public async Task Listen_that_begins_paused_writes_nothing()
    {
        // Not initialized: any measurement would fail, on any platform.
        var monitor = new UserInputMonitor(NullLogger<UserInputMonitor>.Instance, new UserInputMonitorOptions
        {
            PausedUntil = now => now.AddHours(1),
        });

        await monitor.Begin();
        await monitor.End();
    }
}
