using System.Net;
using System.Net.Sockets;
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
    public void Proxy_on_lan_needs_an_explicit_trust_entry()
    {
        // LAN-клиент не должен называться чужим адресом: иначе он подделает
        // X-Forwarded-For и получит чужой мега-IP.
        var resolved = WebClientAddress.Resolve(
            ProxyOnLan, With("X-Forwarded-For", "198.51.100.7"));

        Assert.Equal(ProxyOnLan, resolved);
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("192.168.1.0/24")]
    public void Explicit_trusted_proxy_gets_the_forwarded_address(string entry)
    {
        var headers = With("X-Forwarded-For", "198.51.100.7");

        Assert.Equal(
            Visitor,
            WebClientAddress.Resolve(ProxyOnLan, headers, new[] { entry }));
        Assert.True(WebClientAddress.IdentifiesVisitor(
            ProxyOnLan, headers, new[] { entry }));
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
    [InlineData("::1")]
    public void Trusted_peers_by_default_are_loopback_only(string peer)
    {
        Assert.True(
            WebClientAddress.IsTrustedProxyPeer(IPAddress.Parse(peer)));
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.5")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.7.30")]
    [InlineData("169.254.1.5")]
    public void Private_lan_peers_are_not_trusted_without_the_list(string peer)
    {
        Assert.False(
            WebClientAddress.IsTrustedProxyPeer(IPAddress.Parse(peer)));
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.5")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.7.30")]
    [InlineData("169.254.1.5")]
    public void Explicit_list_trusts_lan_peers(string peer)
    {
        var trusted = new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16" };
        Assert.True(
            WebClientAddress.IsTrustedProxyPeer(IPAddress.Parse(peer), trusted));
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
        // Dual-mode сокет: IPv4-loopback приходит как ::ffff:127.0.0.1.
        var peer = IPAddress.Parse("::ffff:127.0.0.1");
        Assert.True(WebClientAddress.IsTrustedProxyPeer(peer));

        var resolved = WebClientAddress.Resolve(
            peer, With("X-Forwarded-For", "198.51.100.7"));
        Assert.Equal(Visitor, resolved);
    }

    [Fact]
    public void Mapped_ipv6_forwarded_address_is_normalised()
    {
        // Прокси на dual-mode сокете может прислать «::ffff:198.51.100.7».
        // Тот же человек должен получить один бакет, а не два.
        var resolved = WebClientAddress.Resolve(
            ProxyOnHost, With("X-Forwarded-For", "::ffff:198.51.100.7"));

        Assert.Equal(Visitor, resolved);
        Assert.Equal(AddressFamily.InterNetwork, resolved!.AddressFamily);
    }

    [Fact]
    public void Real_ip_is_trimmed()
    {
        var resolved = WebClientAddress.Resolve(
            ProxyOnHost, With("X-Real-IP", "  198.51.100.7  "));

        Assert.Equal(Visitor, resolved);
    }

    [Fact]
    public void A_crowd_behind_one_proxy_gets_one_bucket_each()
    {
        // Сквозной сценарий #62: один прокси, много людей, у каждого свой лимит.
        // Общий бакет на всех — это и был массовый 429.
        var limiter = new WebRateLimiter
        {
            RequestsPerSecond = 30,
            RefusalsBeforeBlock = 5,
            RefusalWindowSec = 10,
            BlockDurationSec = 60,
        };

        var visitors = new[] { "198.51.100.7", "198.51.100.8", "203.0.113.9", "203.0.113.10" };
        var trusted = new[] { "192.168.1.0/24" };
        foreach (var visitor in visitors)
        {
            var address = WebClientAddress.Resolve(
                ProxyOnLan, With("X-Forwarded-For", visitor), trusted);
            Assert.NotNull(address);

            // Страница гостя: запрос + четыре её параллельных запроса.
            for (var i = 0; i < 5; i++)
                Assert.True(limiter.Check(address).Allowed, $"гость {visitor} отказан на {i + 1}-м запросе");
        }

        // Адрес прокси в лимитах вообще не участвует.
        Assert.Equal(4, limiter.TrackedCount);
    }

    [Fact]
    public void Only_a_trusted_proxy_naming_a_client_identifies_visitors()
    {
        // Заголовок от явно доверенного прокси — посетителей различаем.
        Assert.True(WebClientAddress.IdentifiesVisitor(
            ProxyOnLan, With("X-Forwarded-For", "198.51.100.7"),
            new[] { "192.168.1.10" }));
        Assert.True(WebClientAddress.IdentifiesVisitor(
            ProxyOnHost, With("X-Real-IP", "198.51.100.7")));

        // LAN-прокси без записи в trustedProxies, нет заголовка, пришёл напрямую
        // из интернета или мусор — адрес общий.
        Assert.False(WebClientAddress.IdentifiesVisitor(
            ProxyOnLan, With("X-Forwarded-For", "198.51.100.7")));
        Assert.False(WebClientAddress.IdentifiesVisitor(ProxyOnLan, null));
        Assert.False(WebClientAddress.IdentifiesVisitor(
            DirectFromInternet, With("X-Forwarded-For", "198.51.100.7")));
        Assert.False(WebClientAddress.IdentifiesVisitor(
            ProxyOnLan, With("X-Forwarded-For", "не-адрес")));
    }

    [Fact]
    public void Null_peer_stays_null()
    {
        // Тесты роутера вызывают контекст без сокета — «улучшать» нечего.
        Assert.Null(WebClientAddress.Resolve(null, With("X-Forwarded-For", "198.51.100.7")));
        Assert.Null(WebClientAddress.Resolve(null, null));
    }
}
