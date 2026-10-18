using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Polly.Timeout;
using Spectre.Console;

namespace Flextime.Daemon;

public class Sync(IHttpClientFactory httpClientFactory, Computer computer)
{
    public enum DayStatus
    {
        Synced,
        LocalOnly,
        CanSync,
        InSync,
        ServerHasMore,
    }

    // A day this old cannot change on this machine any more, so a pass
    // that only wants to notice new local data needs no more than this.
    private const int WindowDays = 7;

    // Only a compare against every day can notice rows the server holds
    // and this machine does not, so the full history is still checked —
    // just not once a minute.
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromDays(1);

    private readonly MeasurementsFormatter formatter = new(TimeSpan.FromMinutes(10), 0);

    private readonly HttpClient httpClient = httpClientFactory.CreateClient("ApiHttpClient");

    // Where the data and state live; tests point them elsewhere.
    public string Folder { get; init; } = Constants.MeasurementsFolder;
    public string ReconcilePath { get; init; } = StateFiles.ReconcilePath;
    public string LocationPath { get; init; } = StateFiles.LocationPath;
    public string LocationClearPath { get; init; } = StateFiles.LocationClearPath;
    public Func<bool> ListenRunning { get; init; } = () => SingleInstance.IsHeld(SingleInstance.ListenLockPath);

    public async Task SyncAndPrint()
    {
        var since = DueWindow();
        var inSync = 0;

        await ActOnRemoteStatus(upload: true, (line, status) =>
        {
            if (status == DayStatus.InSync)
            {
                // A recurring install runs this once a minute, and by
                // then almost every day is in sync.  Printing them all
                // every pass is what fills the sync log; the count below
                // says they were still checked.
                inSync++;
                return;
            }

            AnsiConsole.WriteLine(TerminalText.Clean(line));
        }, since: since, note: (line, _) => AnsiConsole.WriteLine(TerminalText.Clean(line)));

        AnsiConsole.WriteLine(since.HasValue
            ? $"{inSync} days in sync since {since.Value:yyyy-MM-dd}."
            : inSync == 1
                ? "1 day in sync."
                : $"{inSync} days in sync.");
    }

    public Task SyncAndLog(ILogger logger)
    {
        return ActOnRemoteStatus(upload: true, (line, status) =>
        {
            switch (status)
            {
                case DayStatus.InSync:
                    // Logging every day as in sync on every pass would be noise.
                    break;

                case DayStatus.ServerHasMore:
                    logger.LogWarning("{Line}", TerminalText.Clean(line));
                    break;

                default:
                    logger.LogInformation("{Line}", TerminalText.Clean(line));
                    break;
            }
        }, since: DueWindow(), note: (line, warning) =>
        {
            if (warning)
            {
                logger.LogWarning("{Line}", TerminalText.Clean(line));
            }
            else
            {
                logger.LogInformation("{Line}", TerminalText.Clean(line));
            }
        });
    }

    // Null when the whole history is due to be compared again, otherwise
    // the first day the next pass needs to look at.
    private DateOnly? DueWindow()
    {
        var last = StateFiles.TryRead(ReconcilePath) is [var text, ..]
                   && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                       out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        return DueWindow(last, DateTimeOffset.UtcNow, DateOnly.FromDateTime(DateTime.Today));
    }

    /// <summary>
    /// Split from reading the stamp so the decision can be tested without
    /// a file.  A missing stamp reads as <see cref="DateTimeOffset.MinValue"/>,
    /// which is always due.
    /// </summary>
    public static DateOnly? DueWindow(DateTimeOffset last, DateTimeOffset now, DateOnly today)
    {
        // A stamp ahead of now was written by a clock that was wrong at
        // the time.  Left to age it never becomes due again, so the full
        // compare would stop for as long as the stamp stays ahead — and
        // windowed passes would keep succeeding, so nothing would look
        // broken.  Treat it as due instead.
        if (last > now || now - last >= ReconcileInterval)
        {
            return null;
        }

        return today.AddDays(-WindowDays);
    }

    public Task Print(int count)
    {
        return ActOnRemoteStatus(upload: false, (line, _) => AnsiConsole.WriteLine(TerminalText.Clean(line)), limit: count);
    }

    // Places are synced once location has ever been turned on or off —
    // only then can the files hold any, and after off they still need
    // uploading — or while a clear waits for the server.  Otherwise
    // every request is what it was before places existed, and no file
    // is read twice to find out.
    private bool PlacesActive() =>
        Location.WasEverSet(LocationPath)
        || Location.ReadClear(LocationClearPath)?.Pending == true;

    private async Task<SummaryDataContract?> GetSummary(string version, DateOnly? since) =>
        await httpClient.GetFromJsonAsync(
            SummaryUri(version, since),
            SummarySourceGenerationContext.Default.SummaryDataContract);

    private string SummaryUri(string version, DateOnly? since) =>
        since.HasValue
            ? $"/{Uri.EscapeDataString(computer.Id!)}/summary?api-version={version}&since={since.Value:yyyy-MM-dd}"
            : $"/{Uri.EscapeDataString(computer.Id!)}/summary?api-version={version}";

    // The 1.5 summary is the 1.4 one with place watermarks.  An API
    // without it answers 400 or 404; activity then syncs from 1.4 and
    // places wait for the next round.
    private async Task<(SummaryDataContract? summary, bool places)> GetPlacesSummary(DateOnly? since, Action<string, bool> note)
    {
        using var response = await httpClient.GetAsync(SummaryUri("1.5", since));

        if (response.IsSuccessStatusCode)
        {
            return (await response.Content.ReadFromJsonAsync(SummarySourceGenerationContext.Default.SummaryDataContract), true);
        }

        note($"Places not synced, trying again next sync: the server answered {(int)response.StatusCode} to api-version 1.5.", true);

        return (await GetSummary("1.4", since), false);
    }

    // The hostname this process last had taken, and the day; the server
    // moves the day it was last seen at most once a day, so asking more
    // often than that only costs requests.
    private (string Hostname, DateOnly Day)? reported;

    // The hostname, read live, goes to the server, which decides whether
    // it is worth a write; a renamed machine is known by its new hostname
    // after one pass.  The name the user gives the computer belongs to
    // the server and is never sent from here.  An API from before 1.7
    // takes the hostname the old way, as the name, and only when the
    // summary says it holds something else.  A failure is noted and the
    // pass goes on: the activity matters more than a hostname.
    private async Task ReportHostname(string? held, Action<string, bool> note)
    {
        var hostname = computer.Hostname;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (string.IsNullOrEmpty(hostname) || reported == (hostname, today))
        {
            return;
        }

        var path = $"/{Uri.EscapeDataString(computer.Id!)}";

        try
        {
            using var response = await httpClient.PutAsJsonAsync(
                $"{path}/hostname?api-version=1.7", hostname, StringSourceGenerationContext.Default.String);

            if (response.IsSuccessStatusCode)
            {
                reported = (hostname, today);

                return;
            }

            if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed))
            {
                note($"Hostname not sent, trying again next sync: the server answered {(int)response.StatusCode}.", true);

                return;
            }

            if (held != hostname)
            {
                using var legacy = await httpClient.PatchAsJsonAsync(
                    $"{path}/name", hostname, StringSourceGenerationContext.Default.String);

                if (!legacy.IsSuccessStatusCode)
                {
                    note($"Hostname not sent, trying again next sync: the server answered {(int)legacy.StatusCode}.", true);

                    return;
                }
            }

            reported = (hostname, today);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException)
        {
            note($"Hostname not sent, trying again next sync: {exception.Message}", true);
        }
    }

    private async Task ActOnRemoteStatus(bool upload, Action<string, DayStatus> output, int limit = int.MaxValue,
        DateOnly? since = null, Action<string, bool>? note = null)
    {
        note ??= (_, _) => { };

        var places = upload && PlacesActive();

        SummaryDataContract? remoteSummary;

        if (places)
        {
            (remoteSummary, places) = await GetPlacesSummary(since, note);
        }
        else
        {
            remoteSummary = await GetSummary("1.4", since);
        }

        if (upload)
        {
            await ReportHostname(remoteSummary?.Name, note);
        }

        // A windowed pass compares only days from since on, so only the
        // files that can hold those are opened; a full pass reads all.
        var localByDates = Reader.ReadWindow(Folder, since);

        // (Date, Zone) is unique in the response — the server stores one
        // row per day and zone — so a dictionary probes each group in
        // constant time.  Should a response repeat one anyway, the first
        // is kept rather than failing every pass.
        var remoteByKey = new Dictionary<(DateOnly, string), SummaryItemDataContract>();

        foreach (var item in remoteSummary?.Items ?? [])
        {
            remoteByKey.TryAdd((item.Date, item.Zone), item);
        }

        // A windowed summary knows nothing about days before it, so those
        // days must not be compared against it: every one of them would
        // look missing and be uploaded again on every pass.
        var dates = localByDates
            .Where(item => !since.HasValue || item.Key >= since.Value)
            .TakeLast(limit);

        foreach (var date in dates)
        {
            var summary = formatter.SummarizeDay(date.Value.list.ToArray());

            // Compared per zone, mirroring the storage rows.  A matching
            // watermark — the same number of distinct timestamps, the
            // same last one, and the same sum — is taken to mean the
            // server holds the day.  Sets agreeing on all three can
            // still differ in principle, but only by trading multiple
            // timestamps that cancel out exactly.
            var stale = new List<IGrouping<string, MeasurementWithZone>>();
            var serverHasMore = false;
            var known = false;

            foreach (var byZone in date.Value.list.GroupBy(item => item.Zone))
            {
                var timestamps = byZone
                    .Select(item => item.Measurement.Timestamp)
                    .Distinct()
                    .Order()
                    .ToArray();

                var match = remoteByKey.GetValueOrDefault((date.Key, byZone.Key));

                known |= match != null;

                if (match != null && match.Count == timestamps.Length && match.Last == timestamps[^1]
                    && match.Sum == timestamps.Sum(item => (long)item))
                {
                    continue;
                }

                if (match != null && match.Count > timestamps.Length && match.Last >= timestamps[^1])
                {
                    // The server holds more than this machine: local
                    // files restored from an older backup, a reused
                    // computer id, or rows the old client uploaded with
                    // the whole day under one zone.  Uploading cannot
                    // reconcile that — the merge only adds — so say so
                    // instead of re-uploading forever.
                    serverHasMore = true;
                    continue;
                }

                stale.Add(byZone);
            }

            if (stale.Count == 0)
            {
                output(
                    serverHasMore
                        ? $"{summary} [server has more]"
                        : $"{summary} [in sync]",
                    serverHasMore ? DayStatus.ServerHasMore : DayStatus.InSync);
            }
            else if (upload)
            {
                // The whole day is sent; the server merge is idempotent,
                // so anything it already holds is deduplicated there.
                foreach (var byZone in stale)
                {
                    await Upload(byZone.Key, [.. byZone]);
                }

                output($"{summary} [synced]", DayStatus.Synced);
            }
            else if (known)
            {
                output($"{summary} [can sync]", DayStatus.CanSync);
            }
            else
            {
                output($"{summary} [local only]", DayStatus.LocalOnly);
            }
        }

        if (places)
        {
            // After the activity, which is uploaded by now whatever
            // happens to the places.
            await new PlaceSync(httpClient, computer.Id!, Folder, LocationClearPath, ListenRunning())
                .Run(remoteSummary?.PlacesClearedAt, remoteByKey, since, note);
        }

        if (upload && since is null)
        {
            // Only a pass over every day proves the whole history was
            // compared, so only that one moves the reconcile stamp.
            StateFiles.Write(ReconcilePath, DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    private async Task Upload(string zone, List<MeasurementWithZone> measurements)
    {
        var payload = new MeasurementsDataContract(
            zone,
            measurements
                .Select(item => new MeasurementDataContract((int)item.Measurement.Kind, item.Measurement.Timestamp))
                .ToArray());

        var response = await httpClient.PatchAsJsonAsync(
            $"/{Uri.EscapeDataString(computer.Id!)}",
            payload,
            MeasurementsSourceGenerationContext.Default.MeasurementsDataContract);

        response.EnsureSuccessStatusCode();
    }
}

// The place fields come with api-version 1.5 and read as zero (null)
// from 1.4, or when a (date, zone) has no places.
public record SummaryItemDataContract(
    DateOnly Date,
    string Zone,
    int Count,
    long Last,
    long Sum,
    int PlacesCount = 0,
    long PlacesLast = 0,
    long PlacesSum = 0);

public record SummaryDataContract(string? Name, SummaryItemDataContract[] Items, long? PlacesClearedAt = null);

internal record MeasurementDataContract(int Kind, long Timestamp);
internal record MeasurementsDataContract(string Zone, MeasurementDataContract[] Items);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string))]
internal partial class StringSourceGenerationContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SummaryDataContract))]
internal partial class SummarySourceGenerationContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MeasurementsDataContract))]
internal partial class MeasurementsSourceGenerationContext : JsonSerializerContext;
