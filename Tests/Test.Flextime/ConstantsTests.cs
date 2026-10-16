using Flextime;

namespace Test.Flextime;

public class ConstantsTests
{
    [Theory]
    [InlineData("https://api.example", "https://api.example/")]
    [InlineData("https://api.example/x/y?z=1#f", "https://api.example/")]
    [InlineData("https://api.example:8443/x", "https://api.example:8443/")]
    [InlineData("https://api.example:443/", "https://api.example/")]
    [InlineData("https://user:pw@api.example:8443/x", "https://api.example:8443/")]
    [InlineData("https://user@api.example/", "https://api.example/")]
    public void Keeps_scheme_host_and_port_only(string value, string expected)
    {
        Assert.Equal(expected, Constants.ApiUriFrom(value).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://api.example/")]
    [InlineData("api.example")]
    public void Falls_back_to_the_default(string? value)
    {
        Assert.Equal("https://api.flextime.inhill.com/", Constants.ApiUriFrom(value).ToString());
    }
}
