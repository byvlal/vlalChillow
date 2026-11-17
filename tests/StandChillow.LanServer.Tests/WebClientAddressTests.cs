using System.Net;
using StandChillow.LanServer.Web;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Восстановление адреса клиента за обратным прокси. Лимиты частоты и кап соединений
/// обязаны считать по реальному посетителю: за прокси все сокеты приходят с одного
/// адреса, и общий бакет выжигается толпой — тогда 429 получают все, даже те, кто
/// только открыл сайт.
/// </summary>
public sealed class WebClientAddressTests
{
    private static readonly IPAddress ProxyOnHost = IPAddress.Loopback;
    private static readonly IPAddress ProxyOnLan = IPAddress.Parse("192.168.1.10");
    private static readonly IPAddress DirectFromInternet = IPAddress.Parse("203.0.113.50");
    private static readonly IPAddress Visitor = IPAddress.Parse("198.51.100.7");

    private static IReadOnlyDictionary<string, string> With(
        string name, string value) => new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase) { [name] = value };

    [Fact]
    public void Proxy_on_loopback_trusts_forwarded_for()
    {
        var resolved = WebClientAddress.Resolve(
            ProxyOnHost, With("X-Forwarded-For", "198.51.100.7"));

        Assert.Equal(Visitor, resolved);
    }

    [Fact]
    public void Proxy_on_lan_trusts_forwarded_for()
    {
        var resolved = WebClientAddress.Resolve(
            ProxyOnLan, With("X-Forwarded-For", "198.51.100.7"));

        Assert.Equal(Visitor, resolved);
    }

    [Fact]
    public void Header_name_is_case_insensitive()
    {
        var resolved = WebClientAddress.Resolve(
            ProxyOnHost, With("x-forwarded-for", "198.51.100.7"));

        Assert.Equal(Visitor, resolved);
    }

    [Fact]
    public void Direct_internet_connection_can_not_spoof_the_header()
    {
        // Пришедший напрямую «называет» себя чужим адресом — лимиты считают по сокету.
        var resolved = WebClientAddress.Resolve(
            DirectFromInternet, With("X-Forwarded-For", "198.51.100.7"));

        Assert.Equal(DirectFromInternet, resolved);
    }

    [Fact]
    public void Without_headers_the_socket_address_is_used()
    {
        Assert.Equal(ProxyOnLan, WebClientAddress.Resolve(ProxyOnLan, null));
        Assert.Equal(
            ProxyOnLan,
            WebClientAddress.Resolve(ProxyOnLan, new Dictionary<string, string>()));
    }

    [Fact]
    public void Garbage_header_falls_back_to_the_socket_address()
    {
        var resolved = WebClientAddress.Resolve(
            ProxyOnHost, With("X-Forwarded-For", "не-адрес, ,"));

        Assert.Equal(ProxyOnHost, resolved);
    }

    [Fact]
    public void Forwarded_list_takes_the_first_valid_address()
    {
        // «client, proxy1, proxy2» и вариант прокси, который перезаписывает список
        // одним адресом, — оба читаются как «клиент первый валидный».
        Assert.True(
            WebClientAddress.TryParseClientIp(
                "198.51.100.7, 10.0.0.1, 10.0.0.2", out var chained));
        Assert.Equal(Visitor, chained);

        Assert.True(
            WebClientAddress.TryParseClientIp("  198.51.100.7  ", out var single));
        Assert.Equal(Visitor, single);
    }

    [Fact]
    public void Real_ip_is_the_fallback_header()
    {
        var resolved = WebClientAddress.Resolve(
            ProxyOnHost, With("X-Real-IP", "198.51.100.7"));

        Assert.Equal(Visitor, resolved);
    }

    [Fact]
    public void Forwarded_for_wins_over_real_ip()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Forwarded-For"] = "198.51.100.7",
            ["X-Real-IP"] = "203.0.113.99",
        };
        Assert.Equal(Visitor, WebClientAddress.Resolve(ProxyOnHost, headers));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.5")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.30")]
    [InlineData("169.254.1.5")]
    public void Trusted_peers(string peer)
    {
        Assert.True(
            WebClientAddress.IsTrustedProxyPeer(IPAddress.Parse(peer)));
    }

    [Theory]
    [InlineData("203.0.113.50")]          // интернет
    [InlineData("100.64.0.1")]            // CGNAT — не наша сеть
    [InlineData("8.8.8.8")]
    public void Untrusted_peers(string peer)
    {
        Assert.False(
            WebClientAddress.IsTrustedProxyPeer(IPAddress.Parse(peer)));
    }

    [Fact]
    public void Mapped_ipv6_peer_is_recognised()
    {
        // Dual-mode сокет: IPv4-клиент приходит как ::ffff:192.168.1.10.
        var peer = IPAddress.Parse("::ffff:192.168.1.10");
        Assert.True(WebClientAddress.IsTrustedProxyPeer(peer));

        var resolved = WebClientAddress.Resolve(
            peer, With("X-Forwarded-For", "198.51.100.7"));
        Assert.Equal(Visitor, resolved);
    }

    [Fact]
    public void Null_peer_stays_null()
    {
        // Тесты роутера вызывают контекст без сокета — «улучшать» нечего.
        Assert.Null(WebClientAddress.Resolve(null, With("X-Forwarded-For", "198.51.100.7")));
        Assert.Null(WebClientAddress.Resolve(null, null));
    }
}
