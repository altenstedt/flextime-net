using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flextime;
using Polly.Timeout;

namespace Flextime.Daemon;

/// <summary>
/// Reconciles hours reported by hand against activity actually measured.
///
/// The reported side comes in as JSON — a date, whole minutes, and an
/// opaque tag — so that whatever produced it (a diary parser, a
/// spreadsheet export, a time-tracking service) stays outside this
/// program.  The measured side comes from the server, which merges the
/// selected computers for us.
///
/// The idle limit comes from the user's stored profile when they have
/// one, because that is the rule the web client applied to the number
/// they wrote down.  Only a user with no stored profile falls back to
/// a flat limit.
/// </summary>
public class Report(IHttpClientFactory httpClientFactory, DeviceCode deviceCode, PolicyClient policyClient)
{
    private readonly HttpClient httpClient = httpClientFactory.CreateClient("ApiHttpClient");

    public const int DefaultIdle = 10;

    /// <summary>
    /// Minutes a day may differ by before it is worth mentioning.
    /// Generous on purpose: measured activity is not billable time, so
    /// a permanent small difference is expected and a tight threshold
    /// would bury the findings that matter.
    /// </summary>
    public const int DefaultThreshold = 30;

    public async Task<int> Invoke(
        string? timesheet,
        DateOnly from,
        DateOnly to,
        string[] machines,
        int? idle,
        bool noProfile,
        int threshold,
        TagLayout tags,
        bool verbose,
        bool json,
        CancellationToken cancellationToken)
    {
        if (!deviceCode.IsAuthenticated)
        {
            Console.Error.WriteLine("You need to log on first. Use the login command to log in.");

            return 2;
        }

        // With nothing named and something on the other end of a pipe,
        // the pipe is what was meant.  Naming neither is the one case
        // that cannot be guessed at — and the case a first run lands
        // in, so it gets a primer rather than a reproach.
        if (timesheet == null && !Console.IsInputRedirected)
        {
            Console.Error.WriteLine(
                """
                Compare the hours you reported against the activity Flextime measured,
                and show where they disagree.

                The reported side is yours to provide: a JSON file with one entry per
                day and tag, written by hand or exported from whatever you report your
                time in.  Flextime has no opinion on where the numbers come from.

                    [
                      { "date": "2026-08-17", "tag": "ClientA", "minutes": 480 },
                      { "date": "2026-08-18", "tag": "ClientA", "minutes": 510 },
                      { "date": "2026-08-18", "minutes": 30 }
                    ]

                The tag is optional and means whatever your reporting means by it — a
                client, a project.  Then:

                    flextimed report --timesheet hours.json --month this

                A timesheet can also be piped in.  See report --help for the window,
                machine and layout options.
                """);

            return 1;
        }

        ReportedEntry[] entries;

        try
        {
            entries = ReadReported(timesheet ?? "-");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            Console.Error.WriteLine(timesheet == null
                ? $"Cannot read the piped timesheet: {exception.Message}"
                : $"Cannot read {timesheet}: {exception.Message}");
            Console.Error.WriteLine(
                """A timesheet is a JSON list of { "date": "2026-08-18", "tag": "ClientA", "minutes": 510 }, tag optional.""");

            return 1;
        }

        try
        {
            var profiles = noProfile
                ? new StoredDocument<IdlePolicy>(null, null)
                : await policyClient.GetProfiles(cancellationToken);

            var marks = noProfile
                ? new StoredDocument<Marks>(null, null)
                : await policyClient.GetMarks(cancellationToken);

            // A flat limit beside a stored profile is a contradiction,
            // not a preference: this command exists to reproduce what
            // the web client showed, and the flag would quietly stop it
            // doing that.  Say so rather than ignore the flag.
            if (idle.HasValue && profiles.Value != null)
            {
                Console.Error.WriteLine(
                    "You have stored idle profiles, so --idle would not match what the web client shows. "
                    + "Drop --idle to use your profiles, or add --no-profile to override them.");

                return 1;
            }

            var flat = idle ?? DefaultIdle;

            var known = await httpClient.GetFromJsonAsync(
                "/computers?api-version=1.1",
                PrintDataSourceGenerationContext.Default.ComputersDataContract,
                cancellationToken);

            // No selection means every computer.  That is deliberately
            // wide: the caller is trusted to know which of their
            // machines the reported hours were written against, and the
            // header below always says which were used.
            var selected = Select(known?.Items ?? [], machines);

            if (selected == null)
            {
                return 1;
            }

            var measured = await Measure(selected, profiles.Value, flat, marks.Value ?? Marks.Empty, cancellationToken);

            // The reported side may carry years; only the window is
            // being reconciled, and an entry outside it is not a
            // finding, it is out of scope.
            var result = Reconcile(
                entries.Where(item => item.Date >= from && item.Date <= to).ToArray(),
                measured.Where(item => item.Key >= from && item.Key <= to)
                    .ToDictionary(item => item.Key, item => item.Value),
                selected.Select(item => new MachineDataContract(item.Id, item.Name)).ToArray(),
                profiles.Value != null ? "profile" : flat.ToString(CultureInfo.InvariantCulture),
                profiles.ETag,
                (marks.Value ?? Marks.Empty).Days.Count,
                from,
                to,
                machines.Length > 0,
                (marks.Value ?? Marks.Empty).Days.ToHashSet(),
                threshold);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    result, ReportSourceGenerationContext.Default.ReportDataContract));
            }
            else
            {
                Print(result, tags, verbose);
            }

            return 0;
        }
        catch (TokenRefreshException exception)
        {
            Console.Error.WriteLine(exception.Message);

            return 2;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            Console.Error.WriteLine($"Network error: {exception.Message}");

            return 1;
        }
    }

    public static ReportedEntry[] ReadReported(string path)
    {
        using var reader = path == "-"
            ? new StreamReader(Console.OpenStandardInput())
            : new StreamReader(File.OpenRead(path));

        var text = reader.ReadToEnd();

        // Nothing at all is worth its own message: the JSON reader's
        // complaint about a missing token says nothing about the empty
        // pipe or empty file that actually happened.
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException("it holds nothing at all.");
        }

        return JsonSerializer.Deserialize(text, ReportSourceGenerationContext.Default.ReportedEntryArray)
               ?? [];
    }

    private static ComputerDataContract[]? Select(ComputerDataContract[] known, string[] machines)
    {
        if (machines.Length == 0)
        {
            return known;
        }

        var selected = new List<ComputerDataContract>();

        foreach (var id in machines)
        {
            var match = known.SingleOrDefault(item => item.Id == id);

            if (match == null)
            {
                Console.Error.WriteLine($"Computer {id} not found on server.");

                return null;
            }

            selected.Add(match);
        }

        return selected.ToArray();
    }

    /// <summary>
    /// Work per civil date, at a flat idle limit.
    ///
    /// One request carries every selected computer: the server merges
    /// them, so this is the same union the web client computes from,
    /// rather than a merge reinvented here.  The response has no date
    /// field — it is a list of (zone, timestamps) rows — so the day a
    /// row belongs to is the date of its first timestamp read in the
    /// row's own zone.  Rows are computed separately and only their
    /// work is summed, because merging the timestamps first would
    /// revive rows that are too short to count on their own.
    /// </summary>
    private async Task<Dictionary<DateOnly, TimeSpan>> Measure(
        ComputerDataContract[] selected,
        IdlePolicy? policy,
        int flat,
        Marks marks,
        CancellationToken cancellationToken)
    {
        var query = string.Join("&", selected.Select(item => $"id={item.Id}"));

        var zones = await httpClient.GetFromJsonAsync(
            $"/ids?api-version=1.2&{query}",
            PrintDataSourceGenerationContext.Default.ZonesDataContract,
            cancellationToken);

        var formatter = new MeasurementsFormatter(TimeSpan.FromMinutes(flat), false, 0);
        var work = new Dictionary<DateOnly, TimeSpan>();

        foreach (var row in zones?.Items ?? [])
        {
            // A zone this machine cannot resolve would silently drop a
            // day, and a dropped day reads as unreported activity.
            // Refuse the run instead.
            if (!TimeZones.TryGet(row.Zone, out var zone))
            {
                throw new InvalidOperationException(
                    $"The server returned measurements in time zone {row.Zone}, which this machine cannot resolve.");
            }

            var timestamps = row.Timestamps
                .Select(item => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(item), zone))
                .OrderBy(item => item)
                .ToArray();

            // The weekday a curve is chosen by is the one the day
            // started on, read in the day's own zone.
            var date = DateOnly.FromDateTime(timestamps[0].Date);

            var day = policy == null
                ? formatter.ComputeDay(timestamps)
                : MeasurementsFormatter.ComputeDay(timestamps, policy.For(date, timestamps[0].DayOfWeek));

            if (day == null)
            {
                continue;
            }

            // A marked day keeps its place — the reconcile still knows
            // the day existed — but contributes nothing, exactly as the
            // web client sums it.
            var counted = marks.IsMarked(date) ? TimeSpan.Zero : day.Work;

            work[date] = work.TryGetValue(date, out var running) ? running + counted : counted;
        }

        return work;
    }

    public static ReportDataContract Reconcile(
        ReportedEntry[] entries,
        Dictionary<DateOnly, TimeSpan> measured,
        MachineDataContract[] machines,
        string idle,
        string? profileVersion,
        int markedDays,
        DateOnly from,
        DateOnly to,
        bool machinesChosen,
        IReadOnlySet<DateOnly>? marked = null,
        int threshold = DefaultThreshold)
    {
        marked ??= new HashSet<DateOnly>();
        var reportedByDate = entries
            .GroupBy(item => item.Date)
            .ToDictionary(item => item.Key, item => item.Sum(x => x.Minutes));

        var findings = new List<FindingDataContract>();
        var quiet = new List<FindingDataContract>();

        foreach (var (date, work) in measured.OrderBy(item => item.Key))
        {
            if (reportedByDate.ContainsKey(date))
            {
                continue;
            }

            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            var finding = new FindingDataContract(
                "measured-not-reported",
                date,
                $"{Format(work)} measured, nothing reported");

            // Weekend work is routinely deliberate and unbilled; it
            // belongs in a footer, not among the findings.  And with
            // every computer in scope by default, personal machines
            // would make this the loudest and least useful line in the
            // report — so it only speaks up when the caller said which
            // machines they meant.
            // A day the user marked as not work is answered: they said
            // it was not work, so its activity is not a finding.
            if (weekend || !machinesChosen || marked.Contains(date))
            {
                quiet.Add(finding);
            }
            else
            {
                findings.Add(finding);
            }
        }

        foreach (var (date, minutes) in reportedByDate.OrderBy(item => item.Key))
        {
            if (!measured.ContainsKey(date))
            {
                findings.Add(new FindingDataContract(
                    "reported-not-measured",
                    date,
                    $"{Format(TimeSpan.FromMinutes(minutes))} reported, nothing measured"));
            }
        }

        foreach (var entry in entries.Where(item => string.IsNullOrEmpty(item.Tag)).OrderBy(item => item.Date))
        {
            findings.Add(new FindingDataContract(
                "reported-untagged",
                entry.Date,
                $"{Format(TimeSpan.FromMinutes(entry.Minutes))} reported with no tag"));
        }

        // Deltas are the second tier: interesting, but never as
        // trustworthy as the categorical findings above, because
        // measured time and billable time are not the same thing and
        // never will be.  Hence a generous threshold — small
        // differences are the normal state, not a discrepancy.
        var deltas = new List<DeltaDataContract>();

        foreach (var date in reportedByDate.Keys.Union(measured.Keys).OrderBy(item => item))
        {
            if (marked.Contains(date))
            {
                continue;
            }

            var reportedMinutes = reportedByDate.GetValueOrDefault(date);
            var measuredMinutes = (int)Math.Round(
                measured.GetValueOrDefault(date).TotalMinutes,
                MidpointRounding.AwayFromZero);

            // A day missing from one side entirely is already a
            // categorical finding; saying it twice adds noise.
            if (!reportedByDate.ContainsKey(date) || !measured.ContainsKey(date))
            {
                continue;
            }

            if (Math.Abs(reportedMinutes - measuredMinutes) < threshold)
            {
                continue;
            }

            deltas.Add(new DeltaDataContract(
                date,
                ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)),
                reportedMinutes,
                measuredMinutes));
        }

        var weeks = reportedByDate.Keys.Union(measured.Keys)
            .Where(date => !marked.Contains(date))
            .GroupBy(date => ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)))
            .Select(group => new WeekDataContract(
                group.Key,
                DateOnly.FromDateTime(ISOWeek.ToDateTime(
                    ISOWeek.GetYear(group.Min().ToDateTime(TimeOnly.MinValue)),
                    group.Key,
                    DayOfWeek.Monday)),
                group.Sum(date => reportedByDate.GetValueOrDefault(date)),
                (int)Math.Round(
                    group.Sum(date => measured.GetValueOrDefault(date).TotalMinutes),
                    MidpointRounding.AwayFromZero)))
            .OrderBy(item => item.Week)
            .ToArray();

        var rollup = entries
            .GroupBy(item => item.Tag ?? string.Empty)
            .Select(item => new RollupDataContract(
                item.Key,
                item.Sum(x => x.Minutes),
                item.GroupBy(x => x.Date)
                    .Select(day => new TagDayDataContract(day.Key, day.Sum(x => x.Minutes)))
                    .OrderBy(day => day.Date)
                    .ToArray()))
            .OrderByDescending(item => item.Minutes)
            .ToArray();

        return new ReportDataContract(
            from,
            to,
            machines,
            machinesChosen,
            idle,
            profileVersion,
            markedDays,
            threshold,
            rollup,
            findings.ToArray(),
            deltas.ToArray(),
            weeks,
            quiet.ToArray());
    }

    // Durations read the way the rest of flextime writes them:
    // zero-padded hours and minutes.  Weekly sums run past a day, so
    // this counts total hours rather than using the hh:mm format
    // string, which stops at 23.
    private static string Format(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}";

    private static string Signed(int minutes) =>
        (minutes >= 0 ? "+" : "-") + Format(TimeSpan.FromMinutes(Math.Abs(minutes)));

    private static string Week(DateOnly date) =>
        $"w/{ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)):00} {date:ddd}";

    private static void Print(ReportDataContract report, TagLayout tags, bool verbose)
    {
        var limit = report.Idle == "profile" ? "your idle profiles" : $"idle {report.Idle} min";
        var marked = report.MarkedDays > 0 ? $", {report.MarkedDays} marked" : string.Empty;

        // Which machines were counted belongs on the page even when
        // they are not listed: with every computer in scope by default,
        // counting the wrong set is the easiest way to be confidently
        // wrong, and a run that does not say cannot be checked later.
        Console.WriteLine(report.Machines.Length == 1
            ? $"{Name(report.Machines[0])}"
            : report.MachinesChosen
                ? $"{report.Machines.Length} computers"
                : $"All {report.Machines.Length} computers");

        if (verbose && report.Machines.Length > 1)
        {
            foreach (var machine in report.Machines)
            {
                Console.WriteLine($"  {Name(machine)}");
            }
        }

        Console.WriteLine($"{report.From:yyyy-MM-dd} .. {report.To:yyyy-MM-dd} | {limit}{marked}");

        if (report.Rollup.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Tags:");

            PrintTags(report, tags);
        }

        if (report.Findings.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Findings:");

            foreach (var finding in report.Findings)
            {
                Console.WriteLine($"{finding.Date:yyyy-MM-dd} {finding.Message} | {Week(finding.Date)}");
            }
        }
        else if (verbose)
        {
            Console.WriteLine();
            Console.WriteLine("Findings: none.");
        }

        // Per-day deltas are detail, not news: the week summary below
        // already carries the same insight, and a dozen rows of small
        // differences bury the findings above them.  Ask for them when
        // a week looks wrong and you want to know which day did it.
        if (verbose)
        {
            Console.WriteLine();

            if (report.Deltas.Length == 0)
            {
                Console.WriteLine($"Deltas over {Format(TimeSpan.FromMinutes(report.Threshold))}: none.");
            }
            else
            {
                Console.WriteLine($"Deltas over {Format(TimeSpan.FromMinutes(report.Threshold))}:");

                foreach (var delta in report.Deltas)
                {
                    Console.WriteLine(
                        $"{delta.Date:yyyy-MM-dd} "
                        + $"{Format(TimeSpan.FromMinutes(delta.ReportedMinutes))} reported – "
                        + $"{Format(TimeSpan.FromMinutes(delta.MeasuredMinutes))} measured | "
                        + $"{Signed(delta.ReportedMinutes - delta.MeasuredMinutes)} {Week(delta.Date)}");
                }
            }
        }

        if (report.Weeks.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Weeks:");

            foreach (var week in report.Weeks)
            {
                // Every other row opens with an ISO date and closes
                // with the week marker; a week row that led with w/NN
                // put the same token on the wrong end of the line.
                Console.WriteLine(
                    $"{week.Monday:yyyy-MM-dd} "
                    + $"{Format(TimeSpan.FromMinutes(week.ReportedMinutes))} reported – "
                    + $"{Format(TimeSpan.FromMinutes(week.MeasuredMinutes))} measured | "
                    + $"{Signed(week.ReportedMinutes - week.MeasuredMinutes)} w/{week.Week:00}");
            }
        }

        // Weekend, marked and unselected-machine activity is routinely
        // deliberate.  It is kept, because a reconcile that hid it
        // would hide the answer to "where did the rest of the day go" —
        // but it is not what the reader came for.
        if (!verbose || report.Quiet.Length == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Not counted as findings:");

        foreach (var finding in report.Quiet)
        {
            Console.WriteLine($"{finding.Date:yyyy-MM-dd} {finding.Message} | {Week(finding.Date)}");
        }
    }

    // Tag names are held to a fixed width so that one long name cannot
    // shift every column in the report.
    private const int TagWidth = 18;

    private static string Fit(string tag)
    {
        var name = string.IsNullOrEmpty(tag) ? "(untagged)" : tag;

        return name.Length <= TagWidth ? name : name[..(TagWidth - 1)] + "\u2026";
    }

    private static string Cell(int minutes) =>
        minutes == 0 ? "    \u2014" : Format(TimeSpan.FromMinutes(minutes));

    private static void Totals(ReportDataContract report)
    {
        foreach (var item in report.Rollup)
        {
            Console.WriteLine($"{Fit(item.Tag),-TagWidth} {Format(TimeSpan.FromMinutes(item.Minutes)),8}");
        }
    }

    private static void PrintTags(ReportDataContract report, TagLayout layout)
    {
        switch (layout)
        {
            case TagLayout.Matrix:
                PrintMatrix(report);

                break;

            case TagLayout.Days:
                foreach (var item in report.Rollup)
                {
                    Console.WriteLine($"{Fit(item.Tag),-TagWidth} {Format(TimeSpan.FromMinutes(item.Minutes)),8}");

                    foreach (var day in item.Days)
                    {
                        Console.WriteLine(
                            $"  {day.Date:yyyy-MM-dd} {Format(TimeSpan.FromMinutes(day.Minutes))} | {Week(day.Date)}");
                    }
                }

                break;

            case TagLayout.Weeks:
                PrintWeeks(report);

                break;

            default:
                Totals(report);

                break;
        }
    }

    private static void PrintMatrix(ReportDataContract report)
    {
        var days = report.Rollup
            .SelectMany(tag => tag.Days.Select(day => day.Date))
            .Distinct()
            .OrderBy(date => date)
            .ToArray();

        var weeks = days
            .Select(date => ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)))
            .Distinct()
            .Count();

        // The grid stops being readable well before it stops fitting,
        // and a layout that was asked for by name should say why it
        // declined rather than wrap into nonsense.
        if (weeks > 1 || report.Rollup.Length > 5)
        {
            Console.WriteLine(
                $"(A matrix needs one week and at most five tags; this range has {weeks} week(s) "
                + $"and {report.Rollup.Length} tag(s). Showing totals.)");

            Totals(report);

            return;
        }

        Console.WriteLine(
            new string(' ', 10)
            + string.Concat(report.Rollup.Select(tag => $"{Fit(tag.Tag),9}"))
            + $"{"total",9}");

        foreach (var date in days)
        {
            var cells = report.Rollup
                .Select(tag => tag.Days.FirstOrDefault(day => day.Date == date)?.Minutes ?? 0)
                .ToArray();

            Console.WriteLine(
                $"{date:yyyy-MM-dd}"
                + string.Concat(cells.Select(minutes => $"{Cell(minutes),9}"))
                + $"{Format(TimeSpan.FromMinutes(cells.Sum())),9}  {Week(date)}");
        }

        Console.WriteLine(
            new string(' ', 10)
            + string.Concat(report.Rollup.Select(tag => $"{Format(TimeSpan.FromMinutes(tag.Minutes)),9}"))
            + $"{Format(TimeSpan.FromMinutes(report.Rollup.Sum(tag => tag.Minutes))),9}");
    }

    private static void PrintWeeks(ReportDataContract report)
    {
        var weeks = report.Rollup
            .SelectMany(tag => tag.Days.Select(day => (tag.Tag, day.Date, day.Minutes)))
            .GroupBy(item => ISOWeek.GetWeekOfYear(item.Date.ToDateTime(TimeOnly.MinValue)))
            .OrderBy(group => group.Key)
            .ToArray();

        // The columns are positional, so they need naming once.
        Console.WriteLine(
            new string(' ', TagWidth + 9)
            + string.Concat(new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }.Select(day => $"{day,6}")));

        foreach (var week in weeks)
        {
            var earliest = week.Min(item => item.Date);
            var monday = earliest.AddDays(-(((int)earliest.DayOfWeek + 6) % 7));

            Console.WriteLine($"{monday:yyyy-MM-dd} w/{week.Key:00}");

            foreach (var tag in week
                .GroupBy(item => item.Tag)
                .OrderByDescending(group => group.Sum(item => item.Minutes)))
            {
                var cells = Enumerable.Range(0, 7)
                    .Select(offset => tag
                        .Where(item => item.Date == monday.AddDays(offset))
                        .Sum(item => item.Minutes));

                // A blank rather than a dash: with a row per tag per
                // week, the days a tag was not touched are the majority
                // of the grid, and filling them draws the eye to
                // nothing.  What is left is the shape of the week.
                Console.WriteLine((
                    $"  {Fit(tag.Key),-TagWidth}{Format(TimeSpan.FromMinutes(tag.Sum(item => item.Minutes))),7}"
                    + string.Concat(cells.Select(minutes =>
                        $"{(minutes == 0 ? string.Empty : Format(TimeSpan.FromMinutes(minutes))),6}")))
                    .TrimEnd());
            }
        }
    }

    private static string Name(MachineDataContract machine) =>
        string.IsNullOrEmpty(machine.Name) ? machine.Id : $"{machine.Name} ({machine.Id})";
}

public record ReportedEntry(DateOnly Date, string? Tag, int Minutes);

public record MachineDataContract(string Id, string? Name);
public record TagDayDataContract(DateOnly Date, int Minutes);
public record RollupDataContract(string Tag, int Minutes, TagDayDataContract[] Days);

/// <summary>How the reported hours are laid out under "Tags:".</summary>
public enum TagLayout
{
    /// <summary>One line per tag: the sum over the whole range.</summary>
    Total,

    /// <summary>Days down, tags across. Only legible for a single week.</summary>
    Matrix,

    /// <summary>Each tag's sum, then the days that make it up.</summary>
    Days,

    /// <summary>A week per block, each tag's days as columns Monday to Sunday.</summary>
    Weeks,
}
public record FindingDataContract(string Kind, DateOnly Date, string Message);
public record DeltaDataContract(DateOnly Date, int Week, int ReportedMinutes, int MeasuredMinutes);
public record WeekDataContract(int Week, DateOnly Monday, int ReportedMinutes, int MeasuredMinutes);

public record ReportDataContract(
    DateOnly From,
    DateOnly To,
    MachineDataContract[] Machines,
    bool MachinesChosen,
    string Idle,
    string? ProfileVersion,
    int MarkedDays,
    int Threshold,
    RollupDataContract[] Rollup,
    FindingDataContract[] Findings,
    DeltaDataContract[] Deltas,
    WeekDataContract[] Weeks,
    FindingDataContract[] Quiet);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReportedEntry[]))]
[JsonSerializable(typeof(ReportDataContract))]
internal partial class ReportSourceGenerationContext : JsonSerializerContext;
