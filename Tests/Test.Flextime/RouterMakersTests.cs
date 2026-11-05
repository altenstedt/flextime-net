using Flextime.Daemon;

namespace Test.Flextime;

public class RouterMakersTests
{
    [Theory]
    [InlineData("d0:21:f9:12:34:56", "Ubiquiti")]
    [InlineData("00:03:93:12:34:56", "Apple, often a phone's hotspot that travels with you")]
    [InlineData("94:83:c4:12:34:56", "GL.iNet, a travel router that goes where you go")]
    [InlineData("00:1c:42:12:34:56", "Parallels, a virtual machine's network")]
    [InlineData("08:00:27:12:34:56", "VirtualBox, a virtual machine's network")]
    [InlineData("52:54:00:12:34:56", "QEMU, a virtual machine's network")]
    [InlineData("00:15:5d:12:34:56", "Hyper-V, a virtual machine's network")]
    [InlineData("da:21:f9:12:34:56", "Unknown, the router uses a random address")]
    [InlineData("00:00:00:12:34:56", "Unknown")]
    public void Describe(string mac, string expected)
    {
        Assert.Equal(expected, RouterMakers.Describe(mac));
    }

    [Fact]
    public void Describe_without_an_address_is_null()
    {
        Assert.Null(RouterMakers.Describe(null));
        Assert.Null(new Router("192.168.4.1", "en0", null).MakerLine);
    }

    [Fact]
    public void The_maker_line_names_the_maker()
    {
        var router = new Router("172.20.12.1", "en0", null, "d0:21:f9:12:34:56");

        Assert.Equal("Router maker    :  Ubiquiti", router.MakerLine);
        Assert.Equal("Ubiquiti", router.Maker);
        Assert.False(router.RandomAddress);
    }

    [Fact]
    public void A_random_address_has_no_maker()
    {
        var router = new Router("172.20.10.1", "en0", null, "da:21:f9:12:34:56");

        Assert.Null(router.Maker);
        Assert.True(router.RandomAddress);
    }

    [Fact]
    public void The_location_help_key_fits_in_80_columns()
    {
        Assert.All(LocationHelp.Key.Split('\n'), line => Assert.True(line.Length <= 80, line));
    }
}
