using System.Net;
using System.Text;
using Flextime.Daemon;
using Microsoft.Extensions.Logging.Abstractions;

namespace Test.Flextime;

// The hostname sync reports, and how the computer commands show names.
public class ComputerNamesTests : IDisposable
{
    private const string ComputerId = "0123456789abcdef";

    private static readonly DateOnly Today = new(2026, 10, 18);

    private readonly string folder = Path.Combine(Path.GetTempPath(), $"flextime-computer-names-tests-{Guid.NewGuid():N}");

    public ComputerNamesTests()
    {
        Directory.CreateDirectory(Path.Combine(folder, "measurements"));
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string? Body)> Requests { get; } = [];

        public string Summary { get; set; } = """{"name":"Old-Mac","items":[]}""";

        public HttpStatusCode Hostname { get; set; } = HttpStatusCode.NoContent;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var uri = request.RequestUri!.PathAndQuery;

            Requests.Add((request.Method, uri, body));

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Summary, Encoding.UTF8, "application/json") };
            }

            return new HttpResponseMessage(uri.Contains("/hostname?") ? Hostname : HttpStatusCode.NoContent);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.example/") };
    }

    private Sync CreateSync(FakeApi api) =>
        new(new Factory(api), new Computer(ComputerId, "Martin's MacBook Pro"))
        {
            Folder = Path.Combine(folder, "measurements"),
            ReconcilePath = Path.Combine(folder, "reconcile.txt"),
            LocationPath = Path.Combine(folder, "location.txt"),
            LocationClearPath = Path.Combine(folder, "location-clear.txt"),
            ListenRunning = () => false,
        };

    private static (HttpMethod, string, string?)[] Writes(FakeApi api) =>
        api.Requests.Where(request => request.Method != HttpMethod.Get).Select(request => (request.Method, request.Uri, request.Body)).ToArray();

    [Fact]
    public async Task Sync_reports_the_hostname_and_never_the_name()
    {
        var api = new FakeApi();

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal(
            [(HttpMethod.Put, $"/{ComputerId}/hostname?api-version=1.7", "\"Martin\\u0027s MacBook Pro\"")],
            Writes(api));
    }

    [Fact]
    public async Task One_process_reports_the_same_hostname_once_a_day()
    {
        var api = new FakeApi();
        var sync = CreateSync(api);

        await sync.SyncAndLog(NullLogger.Instance);
        await sync.SyncAndLog(NullLogger.Instance);

        Assert.Single(Writes(api));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task An_api_without_1_7_gets_the_hostname_as_the_name(HttpStatusCode status)
    {
        var api = new FakeApi { Hostname = status };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Equal(
            [
                (HttpMethod.Put, $"/{ComputerId}/hostname?api-version=1.7", "\"Martin\\u0027s MacBook Pro\""),
                (HttpMethod.Patch, $"/{ComputerId}/name", "\"Martin\\u0027s MacBook Pro\""),
            ],
            Writes(api));
    }

    [Fact]
    public async Task An_api_without_1_7_that_holds_the_hostname_gets_nothing_more()
    {
        var api = new FakeApi { Hostname = HttpStatusCode.BadRequest, Summary = """{"name":"Martin's MacBook Pro","items":[]}""" };

        await CreateSync(api).SyncAndLog(NullLogger.Instance);

        Assert.Single(Writes(api));
    }

    [Fact]
    public async Task A_failed_hostname_does_not_fail_the_pass()
    {
        var api = new FakeApi { Hostname = HttpStatusCode.ServiceUnavailable };
        var sync = CreateSync(api);

        await sync.SyncAndLog(NullLogger.Instance);

        // Not taken, so the next pass tries again.
        await sync.SyncAndLog(NullLogger.Instance);

        Assert.Equal(2, Writes(api).Length);
    }

    [Fact]
    public void The_hostname_is_read_from_the_system()
    {
        var hostname = Hostname.Read();

        Assert.False(string.IsNullOrWhiteSpace(hostname));
        Assert.Equal(hostname.Trim(), hostname);
    }

    private static ComputerNamesDataContract Item(
        string id, string? name, string? hostname, DateOnly? first, DateOnly? last,
        NameChangeDataContract[]? names = null, HostnameSeenDataContract[]? hostnames = null) =>
        new(id, name, hostname, name ?? hostname ?? id, names ?? [], hostnames ?? [], first, last);

    [Fact]
    public void The_list_puts_dormant_computers_last_and_marks_this_one()
    {
        ComputerNamesDataContract[] items =
        [
            Item("0a1b2c3d4e5f6a7b", "Old iMac", "iMac", new DateOnly(2019, 2, 1), new DateOnly(2022, 11, 30)),
            Item(ComputerId, "Work 2024", "Martin's MacBook Pro", new DateOnly(2024, 3, 1), new DateOnly(2026, 10, 17)),
            Item("1111222233334444", null, "ThinkPad", new DateOnly(2025, 1, 1), new DateOnly(2026, 9, 1)),
        ];

        Assert.Equal(
            [
                "Id                Name                  Hostname            Data",
                "0123456789abcdef  Work 2024             Martin's MacBook …  2024-03 – 2026-10 *",
                "1111222233334444  ThinkPad                                  2025-01 – 2026-09",
                "",
                "Not used in 90 days:",
                "0a1b2c3d4e5f6a7b  Old iMac              iMac                2019-02 – 2022-11",
            ],
            ComputerClient.FormatList(items, ComputerId, Today));
    }

    [Fact]
    public void Every_list_line_fits_80_columns()
    {
        var item = Item(new string('f', 16), new string('n', 256), new string('h', 256), new DateOnly(2019, 2, 1), Today);

        Assert.All(ComputerClient.FormatList([item], item.Id, Today), line => Assert.InRange(line.Length, 0, 80));
    }

    [Theory]
    [InlineData(2026, 7, 20, false)]
    [InlineData(2026, 7, 19, true)]
    public void Dormant_is_no_data_in_90_days(int year, int month, int day, bool dormant)
    {
        var item = Item(ComputerId, null, "Mac", new DateOnly(2024, 1, 1), new DateOnly(year, month, day));

        Assert.Equal(dormant, ComputerClient.IsDormant(item, Today));
    }

    [Fact]
    public void A_computer_without_data_is_dormant()
    {
        Assert.True(ComputerClient.IsDormant(Item(ComputerId, null, "Mac", null, null), Today));
    }

    [Fact]
    public void Former_names_are_newest_first_without_the_current_ones()
    {
        var item = Item(ComputerId, "Work 2024", "Martin's MacBook Pro", null, null,
            names:
            [
                new NameChangeDataContract("Laptop", DateTimeOffset.Parse("2025-01-01T00:00:00Z")),
                new NameChangeDataContract(null, DateTimeOffset.Parse("2025-06-01T00:00:00Z")),
                new NameChangeDataContract("Work 2024", DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            ],
            hostnames:
            [
                new HostnameSeenDataContract("Martins-MacBook-Pro", new DateOnly(2024, 3, 1), new DateOnly(2026, 10, 16)),
                new HostnameSeenDataContract("Martin's MacBook Pro", new DateOnly(2026, 10, 18), new DateOnly(2026, 10, 18)),
            ]);

        Assert.Equal(["Laptop", "Martins-MacBook-Pro"], ComputerClient.Former(item));
    }

    [Fact]
    public void Show_says_when_no_name_is_given()
    {
        var item = Item(ComputerId, null, "Mac", new DateOnly(2024, 3, 1), new DateOnly(2026, 10, 17));

        Assert.Equal(
            [
                "Name            :  None given; shown by its hostname",
                "Data            :  2024-03-01 – 2026-10-17",
            ],
            ComputerClient.FormatShow(item));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Work 2024", true)]
    [InlineData("Tab\there", false)]
    public void A_name_is_printable(string name, bool valid)
    {
        Assert.Equal(valid, ComputerClient.IsValidName(name));
    }
}
