using System.Net;
using StandChillow.LanServer.Net;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Адрес на проводе: IPv4-mapped IPv6 и обычный IPv4 — один хост, а разные
/// native IPv6 — нет. Сравнение нужно бан-кику: по ошибке здесь можно выкинуть
/// чужого игрока с другим адресом.
/// </summary>
public sealed class AddressMatchingTests
{
    [Theory]
    [InlineData("192.168.1.37", "192.168.1.37", true)]
    [InlineData("192.168.1.37", "::ffff:192.168.1.37", true)]
    [InlineData("::ffff:192.168.1.37", "192.168.1.37", true)]
    [InlineData("2001:db8::1", "2001:db8::1", true)]
    [InlineData("fe80::1", "fe80::1", true)]
    [InlineData("2001:db8::1", "2001:db9::1", false)]
    [InlineData("fe80::1", "::ffff:0.0.0.1", false)]
    [InlineData("192.168.1.37", "192.168.1.38", false)]
    public void SameAddressOnlyEquatesRealOrMappedForms(
        string left,
        string right,
        bool expected)
    {
        Assert.Equal(
            expected,
            GameMatchHost.SameAddress(IPAddress.Parse(left), IPAddress.Parse(right)));
    }
}
