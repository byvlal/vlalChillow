using System.Net;
using System.Net.Sockets;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Адрес клиента для лимитов и логов панели: прямого соединения с ним может не быть.
/// </summary>
/// <remarks>
/// <para>
/// За обратным прокси все TCP-соединения приходят с одного адреса — машины с прокси.
/// Считать всех одним клиентом нельзя: толпа посетителей выжигает общий лимит частоты и
/// общий кап одновременных соединений, и тогда 429 получают все, даже те, кто только
/// открыл сайт. Поэтому панель восстанавливает адрес клиента из заголовка, который прокси
/// из deploy-примера уже ставит: <c>X-Forwarded-For</c> (запасной — <c>X-Real-IP</c>).
/// </para>
/// <para>
/// Верить заголовку всегда нельзя: его пишет отправитель, а запрос может прийти напрямую,
/// мимо прокси. Доверяем только когда прямой отправитель в списке <c>web.trustedProxies</c>;
/// пусто — только loopback. Приватная сеть сама по себе не доверенная: иначе любой
/// LAN-клиент назвался бы чужим адресом и получил мега-IP. Соединение из интернета
/// напрямую права назвать себя чужим адресом не получает: для него адресом остаётся
/// адрес сокета.
/// </para>
/// </remarks>
public static class WebClientAddress
{
    /// <summary>
    /// Адрес клиента: из <c>X-Forwarded-For</c>, если запрос пришёл от доверенного
    /// прокси, иначе — адрес сокета. <c>null</c> проходит насквозь (тесты без сокета).
    /// </summary>
    public static IPAddress? Resolve(
        IPAddress? directPeer,
        IReadOnlyDictionary<string, string>? headers)
        => Resolve(directPeer, headers, trustedProxies: null);

    /// <summary>
    /// Адрес клиента с явным списком доверенных прокси. Пустой список — только loopback:
    /// доверять заголовку от любого LAN-клиента нельзя, иначе он назовётся мега-IP.
    /// </summary>
    public static IPAddress? Resolve(
        IPAddress? directPeer,
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyList<string>? trustedProxies)
    {
        if (directPeer is null || headers is null
            || !IsTrustedProxyPeer(directPeer, trustedProxies))
            return Normalize(directPeer);

        if (headers.TryGetValue("X-Forwarded-For", out var forwarded)
            && TryParseClientIp(forwarded, out var client))
            return Normalize(client);

        if (headers.TryGetValue("X-Real-IP", out var real)
            && IPAddress.TryParse(real.Trim(), out var realIp))
            return Normalize(realIp);

        return Normalize(directPeer);
    }

    /// <summary>
    /// Различает ли восстановленный адрес отдельных посетителей.
    /// </summary>
    /// <remarks>
    /// <c>true</c> только когда запрос пришёл от доверенного прокси и он назвал адрес
    /// клиента заголовком. Во всех остальных случаях адрес — общий: либо это адрес прокси,
    /// за которым стоит весь интернет, либо прямой гость мимо прокси. Общему адресу нельзя
    /// включать долгую паузу (см. <see cref="WebRateLimiter.Check"/>), иначе один поток
    /// запросов погасит сайт для всех за тем же адресом.
    /// </remarks>
    public static bool IdentifiesVisitor(
        IPAddress? directPeer,
        IReadOnlyDictionary<string, string>? headers)
        => IdentifiesVisitor(directPeer, headers, trustedProxies: null);

    /// <summary>То же с явным списком доверенных прокси (пусто — только loopback).</summary>
    public static bool IdentifiesVisitor(
        IPAddress? directPeer,
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyList<string>? trustedProxies)
    {
        if (directPeer is null || headers is null
            || !IsTrustedProxyPeer(directPeer, trustedProxies))
            return false;

        if (headers.TryGetValue("X-Forwarded-For", out var forwarded)
            && TryParseClientIp(forwarded, out _))
            return true;

        return headers.TryGetValue("X-Real-IP", out var real)
            && IPAddress.TryParse(real.Trim(), out _);
    }

    /// <summary>
    /// Один адрес — одно написание. Dual-mode сокет отдаёт IPv4-клиента как
    /// <c>::ffff:a.b.c.d</c>: без схлопывания тот же человек получил бы два разных бакета
    /// в лимитах и две разные записи в банах.
    /// </summary>
    internal static IPAddress? Normalize(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;

    /// <summary>Первый валидный адрес в списке X-Forwarded-For («client, proxy1, …»).</summary>
    internal static bool TryParseClientIp(string? forwarded, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(forwarded))
            return false;
        foreach (var raw in forwarded.Split(','))
        {
            if (IPAddress.TryParse(raw.Trim(), out var parsed) && parsed.AddressFamily is
                    AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            {
                address = parsed;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Прямой отправитель — доверенный прокси: запись из списка (IP/CIDR), а без списка —
    /// только loopback.
    /// </summary>
    internal static bool IsTrustedProxyPeer(IPAddress? address)
        => IsTrustedProxyPeer(address, trustedProxies: null);

    /// <summary>
    /// Доверенный прокси по явному списку IP/CIDR. Пустой список — только loopback:
    /// так LAN-клиент не может подделать заголовок и получить чужой мега-IP.
    /// </summary>
    internal static bool IsTrustedProxyPeer(
        IPAddress? address, IReadOnlyList<string>? trustedProxies)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (trustedProxies is { Count: > 0 })
        {
            foreach (var entry in trustedProxies)
            {
                if (MatchesProxyEntry(address, entry))
                    return true;
            }
            return false;
        }

        return IPAddress.IsLoopback(address);
    }

    /// <summary>Адрес входит в запись списка: точный IP или CIDR (<c>192.168.1.0/24</c>).</summary>
    internal static bool MatchesProxyEntry(IPAddress address, string? entry)
    {
        var text = (entry ?? "").Trim();
        if (text.Length == 0)
            return false;

        var slash = text.IndexOf('/');
        if (slash < 0)
        {
            return IPAddress.TryParse(text, out var exact)
                   && Normalize(exact)?.Equals(address) == true;
        }

        if (!IPAddress.TryParse(text[..slash].Trim(), out var network)
            || !int.TryParse(text[(slash + 1)..].Trim(), out var prefix))
            return false;

        if (network.IsIPv4MappedToIPv6)
            network = network.MapToIPv4();
        if (network.AddressFamily != address.AddressFamily)
            return false;

        var maxBits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefix < 0 || prefix > maxBits)
            return false;

        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        var fullBytes = prefix / 8;
        var remainder = prefix % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != addressBytes[i])
                return false;
        }

        if (remainder == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainder));
        return (networkBytes[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
    }
}
