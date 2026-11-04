using System.Net;
using Flextime.Daemon;
using Polly.Timeout;

namespace Test.Flextime;

public class PrintInfoTests
{
    [Fact]
    public void A_timeout_is_not_reachable_in_plain_words()
    {
        var timeout = new TimeoutRejectedException("The operation didn't complete within the allowed timeout of '00:00:30'.", TimeSpan.FromSeconds(30));

        Assert.Equal("Not reachable, no answer within 30 seconds", PrintInfo.Failure(timeout));
        Assert.Equal("Not fetched, the server is not reachable", PrintInfo.NotFetched(timeout));
    }

    [Fact]
    public void A_refused_connection_says_why()
    {
        var refused = new HttpRequestException("Connection refused (127.0.0.1:9)");

        Assert.Equal("Not reachable, Connection refused (127.0.0.1:9)", PrintInfo.Failure(refused));
    }

    [Fact]
    public void A_server_that_answered_with_an_error_was_reachable()
    {
        var error = new HttpRequestException("Response status code does not indicate success: 503 (Service Unavailable).", null, HttpStatusCode.ServiceUnavailable);

        Assert.Equal("Server error 503", PrintInfo.Failure(error));
        Assert.Equal("Not fetched, the server answered 503", PrintInfo.NotFetched(error));
    }
}
