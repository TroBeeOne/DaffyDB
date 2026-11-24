using DaffySQL.Client;

namespace DaffySQL.Tests;

/// <summary>
/// The connection string keywords DaffyDB depends on beyond logging in: Lock Timeout, which keeps
/// a read blocked behind a writer's locks from holding up every DaffyDB client, and the address
/// a mount is named and shown by.
/// </summary>
public class EndpointTests
{
    private const string Base = "Server=tcp:127.0.0.1,14341;User Id=reader;Password=Secret!1";

    [Fact]
    public void LockTimeoutIsThirtySecondsUnlessSaid()
    {
        var endpoint = ServerEndpoint.Parse(Base);
        Assert.Equal(30, endpoint.LockTimeoutSeconds);
        Assert.Equal("SET LOCK_TIMEOUT 30000; ", endpoint.LockTimeoutPrefix);
        Assert.Equal("lock timeout 30 s", endpoint.DescribeLockTimeout());
    }

    [Theory]
    [InlineData("Lock Timeout=120", 120, "SET LOCK_TIMEOUT 120000; ")]
    [InlineData("lock timeout = 5", 5, "SET LOCK_TIMEOUT 5000; ")]
    [InlineData("Lock Timeout=0", 0, "")]
    public void LockTimeoutIsSecondsAndZeroIsNoLimit(string keyword, int seconds, string prefix)
    {
        var endpoint = ServerEndpoint.Parse($"{Base};{keyword}");
        Assert.Equal(seconds, endpoint.LockTimeoutSeconds);
        Assert.Equal(prefix, endpoint.LockTimeoutPrefix);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("ten")]
    [InlineData("2147484")]
    public void LockTimeoutRefusesWhatIsNotSeconds(string value)
    {
        var error = Assert.Throws<FormatException>(() => ServerEndpoint.Parse($"{Base};Lock Timeout={value}"));
        Assert.Contains("Lock Timeout", error.Message);
    }

    [Fact]
    public void AddressIsWhereAndWhoButNeverThePassword()
    {
        var address = DaffySql.Address($"{Base};Database=Shop");
        Assert.Equal(new SqlServerAddress("127.0.0.1", 14341, "reader", "Shop"), address);
        Assert.DoesNotContain("Secret", address.ToString());
    }

    [Fact]
    public void AddressRefusesWhatConnectWould()
    {
        Assert.Throws<FormatException>(() => DaffySql.Address($"{Base};Colour=blue"));
        Assert.Throws<FormatException>(() => DaffySql.Address("User Id=reader;Password=x"));
    }
}
