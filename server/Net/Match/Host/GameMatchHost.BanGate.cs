using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Баны на канале матча (UDP 7777): IP на коннекте, ник + IP + устройство — до выдачи актора.
/// </summary>
/// <remarks>
/// <para>
/// Проверок ровно две и обе на входе: <c>ConnectionRequest</c> (знаем только адрес) и
/// <c>JoinRoom</c> (знаем ник и стабильный ключ 2.07-установки из handshake). На пакеты
/// состояния ничего не навешивается — <c>WorldObjectState</c>/<c>Rpc</c> идут тем же
/// путём, что и раньше.
/// </para>
/// <para>
/// Гейт в <c>JoinRoom</c> стоит <b>до</b> <c>EnsureRoom</c> и <c>TryFindRejoinActor</c>
/// намеренно: реконнект внутри матча не проходит обычный join, он усыновляет старого
/// актора. Без проверки там читер мог зайти под VPN, выключить его и вернуться
/// реконнектом с уже забаненного адреса или с новым ником.
/// </para>
/// <para>
/// Устройство — стабильная часть 2.07-токена (<c>LB&lt;md5&gt;</c>). Она не зависит от ника
/// и IP, поэтому ловит «сменил ник + включил VPN». Legacy userId устройством не считается.
/// </para>
/// </remarks>
public sealed partial class GameMatchHost
{
    /// <summary>Реестр банов (ставит <c>GameNetHost</c>; в пробе/тестах — null).</summary>
    public BanRegistry? Bans { get; set; }

    /// <summary>
    /// Предикат «этот ник — админский id»: на админов бан не распространяется.
    /// Права считает <c>GameNetHost</c> по <c>admin.ids</c>; здесь только ник записи бана,
    /// потому что гейт банов живёт по нику + IP, а не по id.
    /// </summary>
    public Func<string?, bool>? IsBanExempt { get; set; }

    /// <summary>
    /// Админский id без кода авторизации: в матч не пускаем. Ставит <c>GameNetHost</c>;
    /// id считается из присланного ника, поэтому одного совпадения для прав мало.
    /// </summary>
    public Func<string?, IPAddress?, bool>? IsUnauthorizedAdmin { get; set; }

    /// <summary>IP-гейт самого раннего входа: до handshake, до комнаты, до актора.</summary>
    internal bool IsBannedAddress(IPAddress? address)
    {
        if (Bans is null || address is null)
            return false;

        return Bans.IsIpBanned(address.ToString(), DateTime.UtcNow);
    }

    /// <summary>Полный гейт входа в матч и реконнекта (ник + IP + устройство).</summary>
    internal BanDecision InspectBanJoin(string? nick, IPAddress? address, string? device = null)
    {
        if (Bans is null)
            return BanDecision.Allowed;

        return Bans.Inspect(
            new BanProbe(nick, address?.ToString(), device),
            DateTime.UtcNow,
            IsBanExempt);
    }

    /// <summary>
    /// Все стабильные устройства, которые сейчас сидят в матче под этим ником.
    /// Нужно <c>/ban</c> и веб-админке: по нику находим живой сокет, по сокету — ключ.
    /// </summary>
    public IReadOnlyList<string> DevicesForNick(string? nick)
    {
        var target = ServerConfig.NormalizeNick(nick);
        if (target.Length == 0)
            return Array.Empty<string>();

        var devices = new List<string>();
        foreach (var state in _peers.Values)
        {
            if (string.IsNullOrWhiteSpace(state.DeviceKey)
                || !string.Equals(
                    ServerConfig.NormalizeNick(state.RosterName),
                    target,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            if (!devices.Any(d => MatchIdentity.SamePlayer(d, state.DeviceKey)))
                devices.Add(state.DeviceKey);
        }

        return devices;
    }

    /// <summary>
    /// Отключить забаненного от канала матча (7777). Ищем по нику, адресу и устройствам
    /// сразу: к моменту бана сокет мог ещё не прислать JoinRoom, а ник — уже смениться.
    /// </summary>
    public int DisconnectBanned(
        string? nick,
        IPAddress? address,
        IReadOnlyCollection<string>? devices = null)
    {
        var target = ServerConfig.NormalizeNick(nick);
        var kicked = 0;

        foreach (var (peer, state) in _peers)
        {
            var deviceMatch = devices is not null
                              && state.DeviceKey is { Length: > 0 }
                              && devices.Any(d => MatchIdentity.SamePlayer(d, state.DeviceKey));
            var matched =
                deviceMatch
                || (address is not null && peer.Address is not null && peer.Address.Equals(address))
                || (target.Length > 0
                    && string.Equals(ServerConfig.NormalizeNick(state.RosterName), target,
                        StringComparison.OrdinalIgnoreCase));

            if (!matched)
                continue;

            try
            {
                peer.Disconnect();
                kicked++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ban] match disconnect {peer.Address}: {ex.Message}");
            }
        }

        if (kicked > 0)
        {
            Console.WriteLine(
                $"[ban] матч: отключено сокетов {kicked} (ник '{nick}', IP " +
                $"{address?.ToString() ?? "—"}, устройств {devices?.Count ?? 0})");
        }

        return kicked;
    }
}
