using System.Text.Json;
using Flextime;

namespace Flextime.Daemon;

/// <summary>
/// Reads the idle policy the web client stores: the idle profiles and
/// the days marked as not work.  Both are free-form documents in the
/// server's per-user store, written by the web client and only ever
/// read here — a second writer would race the web's ETags.
/// </summary>
public class PolicyClient(IHttpClientFactory httpClientFactory)
{
    private readonly HttpClient httpClient = httpClientFactory.CreateClient("ApiHttpClient");

    public async Task<StoredDocument<IdlePolicy>> GetProfiles(CancellationToken cancellationToken) =>
        await Get("profiles", IdlePolicy.Decode, cancellationToken);

    public async Task<StoredDocument<Marks>> GetMarks(CancellationToken cancellationToken) =>
        await Get("marks", Marks.Decode, cancellationToken);

    private async Task<StoredDocument<T>> Get<T>(
        string name,
        Func<JsonElement, T> decode,
        CancellationToken cancellationToken)
        where T : class
    {
        var response = await httpClient.GetAsync($"/store/{name}?api-version=1.0", cancellationToken);

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(body);

        // A user with nothing stored gets the literal JSON null, not a
        // 404, and then there is no version to hand out either.
        if (document.RootElement.ValueKind == JsonValueKind.Null)
        {
            return new StoredDocument<T>(null, null);
        }

        var etag = response.Headers.ETag?.ToString();

        try
        {
            return new StoredDocument<T>(decode(document.RootElement), etag);
        }
        catch (IdlePolicyException exception)
        {
            // Refusing is the point.  This runs to reproduce the number
            // the user read in the web client; a document read wrong
            // would reproduce a different one just as confidently.
            throw new IdlePolicyException(
                $"The stored {name} document cannot be read by this version of flextimed: {exception.Message}");
        }
    }
}

/// <summary>A document as stored, with the version it was read at.</summary>
public record StoredDocument<T>(T? Value, string? ETag) where T : class;
