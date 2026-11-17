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
/// мимо прокси. Доверяем только когда прямой отправитель и так свой — loopback или
/// приватная сеть (панель слушает LAN/loopback; прокси из deploy-примера живёт там же).
/// Соединение из интернета напрямую права назвать себя чужим адресом не получает: для
/// него адресом остаётся адрес сокета. Это защита лимитов от случайной толпы, а не от
/// недоброжелателя в своей сети — панель и так без паролей.
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
    {
        if (directPeer is null || headers is null || !IsTrustedProxyPeer(directPeer))
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
    {
        if (directPeer is null || headers is null || !IsTrustedProxyPeer(directPeer))
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
    /// Прямой отправитель — свой: loopback или приватная сеть
    /// (RFC 1918, ULA, link-local). Из интернета напрямую XFF не доверяем.
    /// </summary>
    internal static bool IsTrustedProxyPeer(IPAddress? address)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 (ULA) и fe80::/10 (link-local).
            return (bytes[0] & 0xFE) == 0xFC || (bytes[0] & 0xC0) == 0x80;
        }
        return false;
    }
}
