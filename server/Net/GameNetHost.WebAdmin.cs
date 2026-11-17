using System.Net;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Админские операции из веб-панели: поиск игрока по id, бан, разбан, список банов.
/// </summary>
/// <remarks>
/// <para>
/// Права проверяет <see cref="Web.WebPanelRouter"/> (тройной затвор: сессия сайта →
/// админ из живого конфига → админ-кука этого запуска). Здесь только сама работа:
/// резолв цели, запись в реестр, кик и объявление — тем же путём, что и <c>/ban</c>
/// из чата, чтобы «видят все: кого, кем, почему, на сколько» работало одинаково.
/// </para>
/// </remarks>
public sealed partial class GameNetHost
{
    /// <summary>Справочник личностей — для веб-панели (ник и статус по id).</summary>
    internal PlayerDirectory Directory => _directory;

    /// <summary>Диапазон сроков бана из конфига — для формы в веб-админке.</summary>
    internal (bool Enabled, int MinMinutes, int MaxDays) BanRange
    {
        get
        {
            var s = _config.Admin.Ban;
            return (_bans is not null && s.Enabled, s.MinMinutes, s.MaxDays);
        }
    }

    /// <summary>Активные баны прямо сейчас — для списка в веб-админке.</summary>
    internal IReadOnlyList<BanRecord> ActiveBanRecords()
    {
        if (_bans is null)
            return Array.Empty<BanRecord>();

        return _bans.Active(DateTime.UtcNow);
    }

    /// <summary>
    /// Найти игрока по серверному id для превью: ник, где он сейчас (катка / лобби /
    /// не в сети), аватар, активный бан. Не нашли никого — <c>null</c>.
    /// </summary>
    internal PlayerLookupResult? LookupPlayerForAdmin(int id)
    {
        if (id < BanCommandLine.MinId || id > BanCommandLine.MaxId)
            return null;

        var utc = DateTime.UtcNow;
        var banned = _bans is not null && _config.Admin.Ban.Enabled
            && new PlayerBanLookup(_directory, _bans, IsBanExemptNick).IsBanned(id, utc);
        var custom = CustomIdOf(id);

        // 1) катка (7777): адрес и ник из ростера комнат.
        var inMatch = _match.SnapshotPublicIds().Where(e => e.Id == id).ToList();
        if (inMatch.Count > 1)
        {
            return new PlayerLookupResult(
                id, inMatch[0].Nick, "match", HasAvatarInLobby(id), banned, null, custom,
                $"id {id} делят {inMatch.Count} игрока (совпадение ников) — бан по id невозможен");
        }
        if (inMatch.Count == 1)
            return new PlayerLookupResult(id, inMatch[0].Nick, "match", HasAvatarInLobby(id), banned, null, custom, null);

        // 2) лобби (7778): статус и аватар живут в сессии лобби.
        var inLobby = _session.JoinedPlayers()
            .Where(p => MatchPublicId.ForNick(p.Name) == id)
            .ToList();
        if (inLobby.Count > 1)
        {
            return new PlayerLookupResult(
                id, ServerConfig.NormalizeNick(inLobby[0].Name), "lobby", HasAvatarInLobby(id), banned, null, custom,
                $"id {id} делят {inLobby.Count} игрока в лобби (совпадение ников) — бан по id невозможен");
        }
        if (inLobby.Count == 1)
        {
            return new PlayerLookupResult(
                id, ServerConfig.NormalizeNick(inLobby[0].Name), "lobby", HasAvatarInLobby(id), banned, null, custom, null);
        }

        // 3) не в сети: справочник личностей помнит ник и когда видел последний раз.
        var seen = _directory.Resolve(id);
        if (seen is null)
            return null;

        var sharing = _directory.CountById(id);
        var lastSeen = DateTimeOffset.FromUnixTimeMilliseconds(seen.LastSeenUtcMs).UtcDateTime;
        return new PlayerLookupResult(
            id, seen.Nick, "offline", false, banned, lastSeen, custom,
            sharing > 1 ? $"id {id} делят {sharing} ника (коллизия) — бан по id невозможен" : null);
    }

    /// <summary>Есть ли у игрока аватар (байты из лобби): онлайн-превью в админке.</summary>
    private bool HasAvatarInLobby(int id)
    {
        foreach (var player in _session.JoinedPlayers())
        {
            if (MatchPublicId.ForNick(player.Name) != id)
                continue;
            return player.Avatar is { Length: > 0 };
        }

        return false;
    }

    /// <summary>
    /// Бан из веб-админки: тот же путь, что <c>/ban</c> из чата — проверки, запись,
    /// кик из катки и лобби, объявление всем (кого, кем, почему, на сколько).
    /// </summary>
    /// <returns>Текст ошибки или <c>null</c> при успехе (тогда заполнена запись).</returns>
    internal (string? Error, BanRecord? Record) TryWebBan(
        int id, string durationToken, string reason, string bannedBy)
    {
        if (_bans is null)
            return ("баны не инициализированы", null);
        if (!_config.Admin.Ban.Enabled)
            return ("баны выключены (admin.ban.enabled = false)", null);
        if (id < BanCommandLine.MinId || id > BanCommandLine.MaxId)
            return ("неверный id игрока", null);
        if (string.IsNullOrWhiteSpace(bannedBy))
            return ("неизвестно, кто банит", null);

        var target = ResolveBanTargetById(id);
        if (target.Error is not null)
            return (target.Error, null);

        // Себя не баним: админ-панель знает свой ID из сессии сайта.
        if (id == IdOf(bannedBy))
            return ("себя банить нельзя", null);

        var messages = new List<string>();
        var record = TryBanTarget(
            messages.Add, target.Nick, target.Address, durationToken, reason,
            player: null, id, target.KnownIps, bannedBy: bannedBy);

        if (record is null)
            return (messages.Count > 0 ? messages[0] : "бан не удался", null);

        return (null, record);
    }

    /// <summary>Разбан из веб-админки по серверному id: снимает все записи этого человека.</summary>
    /// <returns>Текст ошибки или <c>null</c> при успехе (тогда — сколько записей снято).</returns>
    internal (string? Error, int Removed) TryWebUnban(int id)
    {
        if (_bans is null)
            return ("баны не инициализированы", 0);

        var messages = new List<string>();
        var removed = RemoveBansById(messages.Add, id);
        if (removed == 0)
            return (messages.Count > 0 ? messages[0] : $"под id {id} активных банов нет", 0);

        return (null, removed);
    }
}
