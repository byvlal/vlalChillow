using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Баны на канале матча (UDP 7777): IP на коннекте, ник + IP + устройство — до выдачи актора.
/// </summary>
/// <remarks>
/// <para>
/// Проверок три: <c>ConnectionRequest</c> (знаем только адрес), <c>JoinRoom</c> (знаем
/// ник и стабильный ключ 2.07-установки из handshake) и периодический свип уже
/// сидящих в катке (<c>admin.ban.matchSweepSec</c>): бан, выданный во время игры,
/// должен выкинуть нарушителя, а не ждать его выхода. На пакеты состояния ничего не
/// навешивается — <c>WorldObjectState</c>/<c>Rpc</c> идут тем же путём, что и раньше.
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
    /// Живой выключатель <c>admin.ban.enabled</c>. Без него реестр продолжал бы банить
    /// даже после того, как баны выключили в конфиге.
    /// </summary>
    public Func<bool>? IsBanEnabled { get; set; }

    /// <summary>Период перепроверки акторов, секунд (<c>admin.ban.matchSweepSec</c>).</summary>
    public Func<int>? BanSweepSeconds { get; set; }

    private long _nextBanSweepUtc;

    private bool BansActive => Bans is not null && (IsBanEnabled?.Invoke() ?? true);

    /// <summary>
    /// Реестр аккаунтов (ставит <c>GameNetHost</c>; в тестах может быть null). Нужен,
    /// чтобы публичный id для бана брался только у владельца устройства, а успешный
    /// вход в катку привязывал аккаунт к актору.
    /// </summary>
    public AccountStore? Accounts { get; set; }

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

    /// <summary>
    /// Проверка ника из <c>JoinRoomRequest.room</c>: в лобби такой ник уже отклонили бы,
    /// но на 7777 клиент присылает его заново. Ставит <c>GameNetHost</c> из конфига
    /// (<c>names.enforce</c> / <c>names.maxLength</c>); null — проверки нет.
    /// </summary>
    public Func<string, bool>? IsNickAllowed { get; set; }

    /// <summary>IP-гейт самого раннего входа: до handshake, до комнаты, до актора.</summary>
    internal bool IsBannedAddress(IPAddress? address)
    {
        if (!BansActive || address is null)
            return false;

        return Bans!.IsIpBanned(address.ToString(), DateTime.UtcNow);
    }

    /// <summary>
    /// Полный гейт входа в матч и реконнекта (ник + IP + устройство + аккаунт).
    /// </summary>
    internal BanDecision InspectBanJoin(string? nick, IPAddress? address, string? device = null) =>
        InspectBanActor(nick, address, device, 0);

    /// <summary>
    /// Та же проверка, но с уже известным номером аккаунта актёра: бан по id должен
    /// срабатывать даже тогда, когда handshake не принёс устройство.
    /// </summary>
    internal BanDecision InspectBanActor(
        string? nick,
        IPAddress? address,
        string? device,
        int accountId)
    {
        if (!BansActive)
            return BanDecision.Allowed;

        // Номер аккаунта от актора — уже подтверждённый ключ. На входе в матч его
        // ещё нет, поэтому второй путь — ProbeIds(device): устройство, привязанное
        // к аккаунту, не даёт чужому новому входу под тем же ником получить чужой
        // бан. У одной установки бывает несколько аккаунтов (разные ники), поэтому
        // проверяем каждый её номер: бан любого из них закрывает вход.
        var publicIds = new List<int>(4);
        if (accountId > 0)
            publicIds.Add(accountId);
        foreach (var id in Accounts?.ProbeIds(device) ?? Array.Empty<int>())
        {
            if (!publicIds.Contains(id))
                publicIds.Add(id);
        }
        if (publicIds.Count == 0)
            publicIds.Add(0);

        BanDecision? learned = null;
        foreach (var publicId in publicIds)
        {
            var decision = Bans!.Inspect(
                new BanProbe(nick, address?.ToString(), device, publicId),
                DateTime.UtcNow,
                IsBanExempt);
            if (decision.Banned || decision.AdminExempt)
                return decision;
            // Оставляем первый ответ, если никто не забанен: в нём LearnedAlias
            // (если проверка что-то выучила) и MatchedBy для лога.
            learned ??= decision;
        }

        return learned ?? BanDecision.Allowed;
    }

    /// <summary>
    /// Account ID актёра. Вызывать можно без внешнего <c>_roomGate</c>: замок
    /// реентрантный, а SetProperty читает id вне его.
    /// </summary>
    internal int AccountIdOfActor(byte actorNr, MatchPeerState? state, MatchRoom? room)
    {
        lock (_roomGate)
        {
            if (room is not null && room.ActorAccounts.TryGetValue(actorNr, out var id))
                return id;
        }

        return state?.AccountId ?? 0;
    }

    /// <summary>Account ID актёра; вызывать под <c>_roomGate</c>.</summary>
    private static int AccountIdOfActorLocked(MatchRoom room, byte actorNr) =>
        room.ActorAccounts.TryGetValue(actorNr, out var id) ? id : 0;

    /// <summary>
    /// Все стабильные устройства, которые сейчас сидят в матче под этим аккаунтом.
    /// Нужно <c>/ban</c> и веб-админке: по аккаунту находим живой сокет, по сокету — ключ.
    /// </summary>
    public IReadOnlyList<string> DevicesForAccount(int accountId)
    {
        if (accountId <= 0)
            return Array.Empty<string>();

        var devices = new List<string>();
        foreach (var state in _peers.Values)
        {
            if (state.AccountId != accountId || string.IsNullOrWhiteSpace(state.DeviceKey))
                continue;

            if (!devices.Any(d => MatchIdentity.SamePlayer(d, state.DeviceKey)))
                devices.Add(state.DeviceKey);
        }

        return devices;
    }

    /// <summary>
    /// Каждые <c>admin.ban.matchSweepSec</c> перепроверяет всех, кто уже в катке.
    /// Забаненный после входа игрок должен вылететь, даже если на handshake его
    /// устройство/IP ещё не были известны (например, бан выдали по аккаунту).
    /// </summary>
    private void TickBanSweep()
    {
        if (!BansActive)
            return;

        var now = Environment.TickCount64;
        var everySeconds = Math.Clamp(BanSweepSeconds?.Invoke() ?? 2, 1, 30);
        if (now < _nextBanSweepUtc)
            return;
        _nextBanSweepUtc = now + everySeconds * 1000L;

        foreach (var (peer, st) in _peers)
        {
            if (st.BannedForMatch)
                continue;

            var decision = InspectBanActor(st.RosterName, peer.Address, st.DeviceKey, st.AccountId);
            if (!decision.Banned || decision.Record is null)
                continue;

            st.BannedForMatch = true;
            Console.WriteLine(
                $"[ban] матч: актор {st.ActorNr} '{st.RosterName ?? "?"}' {peer.Address} — " +
                $"совпало {decision.MatchedBy}, бан №{BanMessages.BanIdOf(decision.Record)}; отключаю");

            NotifyBannedPeer(peer, st, decision.Record);

            if (st.Room is not null && st.ActorNr > 0)
                RemoveActorFromMatch(st.Room, st.ActorNr, st.UserId, st.IdentityKey, "banned");

            DisconnectPeerSoon(peer);
        }
    }

    /// <summary>Серверный чат забаненному в катке: те же строки, что и на входе.</summary>
    private void NotifyBannedPeer(NetPeer peer, MatchPeerState st, BanRecord record)
    {
        if (st.Room is null)
            return;

        try
        {
            var live = LiveChatActors(st.Room);
            foreach (var line in BanMessages.NoticeLines(record, DateTime.UtcNow))
                SendCritical(peer, BuildServerChatPayload(line, st.ActorNr, live));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ban] match notice {peer.Address}: {ex.Message}");
        }
    }

    /// <summary>Разорвать сокет чуть позже: надёжный чат должен успеть уйти.</summary>
    private static void DisconnectPeerSoon(NetPeer peer, int delayMs = 250)
    {
        _ = Task.Delay(Math.Max(0, delayMs)).ContinueWith(_ =>
        {
            try { peer.Disconnect(); }
            catch { /* уже мёртв */ }
        });
    }

    /// <summary>
    /// Отключить от катки всех, кто сидит под этим аккаунтом. Кик из панели:
    /// бан не ставится, флаг <see cref="MatchPeerState.BannedForMatch"/> не трогаем,
    /// иначе повторный вход после кика считался бы банным.
    /// </summary>
    /// <returns>Сколько сокетов матча закрыто.</returns>
    public int DisconnectAccount(int accountId)
    {
        if (accountId <= 0)
            return 0;

        var kicked = 0;
        foreach (var (peer, state) in _peers)
        {
            if (state.AccountId != accountId)
                continue;

            try
            {
                DisconnectPeerSoon(peer);
                kicked++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[admin] kick match {peer.Address}: {ex.Message}");
            }
        }

        if (kicked > 0)
            Console.WriteLine($"[admin] матч: кик аккаунта {accountId} — сокетов {kicked}");

        return kicked;
    }

    /// <summary>
    /// Отключить забаненного от канала матча (7777). Ищем по аккаунту, адресу и
    /// устройствам сразу: к моменту бана сокет мог ещё не прислать JoinRoom, а ник —
    /// уже смениться. Ник ключом не является: тёзка не должен пострадать.
    /// </summary>
    /// <param name="notice">
    /// Запись бана: если передана, перед разрывом игрок получает полное уведомление
    /// (причина, кто забанил, ID, номер бана, срок) прямо в матч-чат.
    /// </param>
    public int DisconnectBanned(
        int accountId,
        IPAddress? address,
        IReadOnlyCollection<string>? devices = null,
        BanRecord? notice = null)
    {
        var kicked = 0;

        foreach (var (peer, state) in _peers)
        {
            var deviceMatch = devices is not null
                              && state.DeviceKey is { Length: > 0 }
                              && devices.Any(d => MatchIdentity.SamePlayer(d, state.DeviceKey));
            var addressMatch = address is not null
                               && peer.Address is not null
                               && SameAddress(peer.Address, address);
            var matched =
                (accountId > 0 && state.AccountId == accountId)
                || deviceMatch
                || addressMatch;

            if (!matched)
                continue;

            state.BannedForMatch = true;
            if (notice is not null)
                NotifyBannedPeer(peer, state, notice);

            try
            {
                if (notice is not null)
                    DisconnectPeerSoon(peer);
                else
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
                $"[ban] матч: отключено сокетов {kicked} (аккаунт {accountId}, IP " +
                $"{address?.ToString() ?? "—"}, устройств {devices?.Count ?? 0})");
        }

        return kicked;
    }
}
