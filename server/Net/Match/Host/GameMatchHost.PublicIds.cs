using System.Globalization;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net;

/// <summary>Строка админ-списка: серверный id, ник, адрес и номер актора.</summary>
public readonly record struct MatchPublicEntry(int Id, string Nick, IPAddress? Address, byte ActorNr);

/// <summary>
/// Кто сейчас в катке — с постоянным id аккаунта.
/// </summary>
/// <remarks>
/// <para>
/// Id выдаётся при входе (<see cref="AccountStore"/>) и хранится в комнате рядом с
/// актором, поэтому переживает реконнект и не зависит от ника. Здесь он нужен для
/// админ-команд <c>/ids</c> и <c>/ban</c>.
/// </para>
/// <para>
/// В провод id не уходит как отдельное поле: клиент его не знает и подменить не может.
/// </para>
/// </remarks>
public sealed partial class GameMatchHost
{
    /// <summary>Игроки в катке: id аккаунта, ник, адрес сокета, номер актора.</summary>
    public IReadOnlyList<MatchPublicEntry> SnapshotPublicIds()
    {
        lock (_roomGate)
        {
            var entries = new List<MatchPublicEntry>();
            foreach (var room in _roomsByPassword.Values.Distinct())
            {
                foreach (var (actorNr, name, _) in room.Actors)
                {
                    if (actorNr == MatchHostActor.ActorNr || string.IsNullOrWhiteSpace(name))
                        continue;

                    var id = AccountIdOfActorLocked(room, actorNr);
                    if (id == 0 && Accounts is null)
                        id = LegacyNickHash.ForNick(name); // тесты/проба без аккаунтов
                    if (id == 0)
                        continue;

                    entries.Add(new MatchPublicEntry(
                        id, name, AddressOfActorLocked(room, actorNr), actorNr));
                }
            }

            return entries.OrderBy(e => e.Id).ToList();
        }
    }

    /// <summary>Найти игрока в катке по серверному id.</summary>
    public bool TryResolvePublicId(int id, out MatchPublicEntry entry)
    {
        foreach (var candidate in SnapshotPublicIds())
        {
            if (candidate.Id != id)
                continue;
            entry = candidate;
            return true;
        }

        entry = default;
        return false;
    }

    /// <summary>
    /// Подменять ли клиентский <c>uid</c> («Offline») на серверный id игрока. По умолчанию да:
    /// тогда в игре вместо заглушки виден номер, который принимает <c>/ban</c>.
    /// </summary>
    public bool ExposeServerUid { get; set; } = true;

    /// <summary>
    /// Серверная галочка «аккаунт настоящий»: ник админа → <c>true</c>. Лобби-хост
    /// подключает свою проверку (<c>admin.ids</c> + код этого запуска). <c>null</c> —
    /// проверка выключена, свойство остаётся как его прислал клиент.
    /// </summary>
    /// <remarks>
    /// Клиенты сами пишут <c>verified</c> в своей identity-пачке, и до сих пор сервер
    /// пересылал это как есть: подмена «isVerified = true» на клиенте выглядела бы
    /// галочкой у всех. Теперь значение перезаписывается: не-админ получает <c>false</c>,
    /// даже если прислал <c>true</c>, а админ — <c>true</c>, даже если клиент прислал
    /// <c>false</c>.
    /// </remarks>
    public Func<string, IPAddress?, bool>? IsAdminVerifiedFor { get; set; }

    /// <summary>
    /// Автоматическая галочка <c>verified</c>: сервер сам выставляет значение по
    /// admin.ids (+ код этого запуска), не дожидаясь клиентского prop — клиент может
    /// его не прислать вовсе. Админ → <c>true</c>, все остальные → <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Релей свойств уже правит подмену на лету (не-админ с <c>true</c> получает
    /// <c>false</c>, админ — <c>true</c> при любом присланном значении). Здесь закрыт
    /// обратный случай: prop не пришёл ни разу (старые клиенты, пробы) — тогда сервер
    /// досылает своё решение сам, чтобы галочка не зависела от поведения клиента.
    /// </remarks>
    private void EnsureServerVerifiedProp(MatchRoom room, byte actorNr, NetPeer authorPeer, string reason)
    {
        if (IsAdminVerifiedFor is null || actorNr == MatchHostActor.ActorNr)
            return;
        if (!_peers.TryGetValue(authorPeer, out var st))
            return;

        var nick = ResolveActorNick(actorNr, st, room);
        var isAdmin = !string.IsNullOrWhiteSpace(nick)
            && IsAdminVerifiedFor(nick, authorPeer.Address);
        var want = LobbyVariant.FromBool(isAdmin);

        lock (_roomGate)
        {
            if (room.ActorProps.TryGetValue((actorNr, MatchRoomPropKeys.Verified), out var have)
                && have.Kind == LobbyVariantKind.Bool
                && have.Bool == want.Bool)
                return; // уже совпадает с решением сервера — лишний SetProperty не шлём
            room.ActorProps[(actorNr, MatchRoomPropKeys.Verified)] = want;
        }

        BroadcastRoom(
            room,
            MatchCodec.BuildSetProperty(NextServerTime(), actorNr, MatchRoomPropKeys.Verified, want),
            tag: "match_tx_SetProperty");
        Console.WriteLine(
            $"[match-host] verified actor={actorNr} — сервер выставил " +
            $"{(isAdmin ? "true (админ)" : "false")} ({reason})");
    }

    /// <summary>
    /// Кастомный отображаемый ID: серверный id (число) → строка, которую видят игроки.
    /// Ставится только админам через веб-панель; оригинальный числовой ID сервер
    /// продолжает использовать для банов и <c>admin.ids</c>.
    /// </summary>
    public Func<int, string?>? CustomIdOf { get; set; }

    /// <summary>
    /// Новое значение <c>uid</c> для автора свойства: постоянный id его ника.
    /// </summary>
    /// <remarks>
    /// Ник берём из состояния сессии (<c>RosterName</c> после JoinRoom), а не из пакета:
    /// свойство может прислать кто угодно и про любого актора, а id должен быть тем же,
    /// что показал <c>/ids</c>. Актор 1 — сам сервер: ему всегда захардкоженный «Server»,
    /// и клиент не может ни присвоить этот id себе, ни подменить его серверу.
    /// </remarks>
    private bool TryServerUid(byte actorNr, MatchPeerState state, MatchRoom room, out LobbyVariant uid)
    {
        uid = default;
        if (!ExposeServerUid)
            return false;

        if (actorNr == MatchHostActor.ActorNr)
        {
            uid = LobbyVariant.FromString(ServerIdentity.Uid);
            return true;
        }

        var id = AccountIdOfActor(actorNr, state, room);
        if (id > 0)
        {
            // Кастомный ID админа: вместо числа — его строка. Оригинальный account ID
            // для банов и admin.ids никуда не девается — подменяется только показ.
            var custom = CustomIdOf?.Invoke(id);
            uid = LobbyVariant.FromString(
                string.IsNullOrWhiteSpace(custom)
                    ? id.ToString(CultureInfo.InvariantCulture)
                    : custom);
            return true;
        }

        // Аккаунты отключены только в тестах/пробе: там допустим прежний расчёт из ника.
        if (Accounts is not null)
            return false;

        var nick = ResolveActorNick(actorNr, state, room);
        if (!LegacyNickHash.TryWireUid(nick, out var text))
            return false;

        uid = LobbyVariant.FromString(text);
        return true;
    }

    private volatile bool _uidResyncPending;

    /// <summary>
    /// Попросить матч пересчитать <c>uid</c> на ближайшем тике: кастомный ID отозван или
    /// выдан, а клиенты уже видят старую строку. Флаг можно ставить из любого потока —
    /// пакеты шлёт только игровой цикл.
    /// </summary>
    internal void RequestUidResync() => _uidResyncPending = true;

    /// <summary>
    /// Разослать актуальный <c>uid</c> всем актёрам, у кого он разошёлся с решением
    /// сервера. Только чтение-сверка и одна рассылка на изменившихся; если никто не
    /// изменился — ни одного пакета.
    /// </summary>
    internal void ResyncActorUids()
    {
        _uidResyncPending = false;
        if (!ExposeServerUid)
            return;

        List<(MatchRoom Room, byte ActorNr, LobbyVariant Uid)> updates = new();
        lock (_roomGate)
        {
            foreach (var room in _roomsByPassword.Values)
            {
                foreach (var (_, st) in _peers)
                {
                    if (!ReferenceEquals(st.Room, room) || st.ActorNr == 0)
                        continue;

                    if (!TryServerUid(st.ActorNr, st, room, out var uid))
                        continue;

                    if (room.ActorProps.TryGetValue((st.ActorNr, MatchRoomPropKeys.Uid), out var have)
                        && have.Kind == uid.Kind
                        && string.Equals(have.String, uid.String, StringComparison.Ordinal))
                        continue;

                    room.ActorProps[(st.ActorNr, MatchRoomPropKeys.Uid)] = uid;
                    updates.Add((room, st.ActorNr, uid));
                }
            }
        }

        foreach (var (room, actorNr, uid) in updates)
        {
            BroadcastRoom(
                room,
                MatchCodec.BuildSetProperty(NextServerTime(), actorNr, MatchRoomPropKeys.Uid, uid),
                tag: "match_tx_SetProperty");
            Console.WriteLine(
                $"[match-host] uid actor={actorNr} обновлён на «{uid}» (права изменились)");
        }
    }

    /// <summary>
    /// Ник автора свойства. Ник берём из состояния сессии (<c>RosterName</c> после
    /// JoinRoom), а не из пакета: свойство может прислать кто угодно и про любого актора.
    /// Свойства может прислать хост за игрока — тогда его состояния под рукой нет
    /// и ник ищем по сессиям комнаты (после JoinRoom он уже записан).
    /// </summary>
    private string? ResolveActorNick(byte actorNr, MatchPeerState state, MatchRoom room)
    {
        var nick = actorNr == state.ActorNr && ReferenceEquals(state.Room, room)
            ? state.RosterName
            : null;

        if (!string.IsNullOrWhiteSpace(nick))
            return nick;

        foreach (var other in _peers.Values)
        {
            if (other.ActorNr != actorNr || !ReferenceEquals(other.Room, room))
                continue;
            if (string.IsNullOrWhiteSpace(other.RosterName))
                continue;
            return other.RosterName;
        }

        return null;
    }

    private IPAddress? AddressOfActorLocked(MatchRoom room, byte actorNr)
    {
        foreach (var (peer, state) in _peers)
        {
            if (ReferenceEquals(state.Room, room) && state.ActorNr == actorNr)
                return peer.Address;
        }

        return null;
    }
}
