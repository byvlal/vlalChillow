using System.Net;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net;

/// <summary>
/// То, что админка сайта делает с игроком: бан, разбан и поиск по id.
/// </summary>
/// <remarks>
/// <para>
/// Отдельный partial, а не код в веб-слое: правила бана (кто неприкосновенен, какой срок
/// допустим, как проинформировать игроков) обязаны совпадать у <c>/ban</c> в чате и у
/// кнопки на сайте. Поэтому все проверки живут здесь, рядом с командой, и вызываются
/// через одни и те же методы реестра.
/// </para>
/// <para>
/// Права админа проверяет вызывающий: сюда попадают только запросы, прошедшие
/// сайтовую сессию <b>и</b> код этого запуска (см. <c>WebPanelRouter.AdminAuth</c>).
/// </para>
/// </remarks>
public sealed partial class GameNetHost
{
    /// <summary>Кто это по серверному id: ник, известные адреса, когда видели.</summary>
    /// <param name="playerId">Числовой id (тот, что в <c>admin.ids</c> и <c>/ids</c>).</param>
    public SeenPlayer? LookupPlayerById(int playerId)
    {
        if (playerId <= 0)
            return null;

        return _directory.Resolve(playerId);
    }

    /// <summary>Активные баны — для списка в админке.</summary>
    public IReadOnlyList<BanRecord> ActiveBans() =>
        _bans?.Active(DateTime.UtcNow) ?? Array.Empty<BanRecord>();

    /// <summary>Забанен ли этот id прямо сейчас (по справочнику ник↔IP).</summary>
    public bool IsBanned(int playerId) => IsPlayerBannedById(playerId);

    /// <summary>
    /// Забанить игрока по серверному id с сайта.
    /// </summary>
    /// <param name="playerId">Числовой id цели.</param>
    /// <param name="duration">
    /// Срок строкой в тех же формах, что и <c>/ban</c>: <c>30m</c>, <c>6h</c>, <c>3d</c>.
    /// </param>
    /// <param name="reason">Причина; пусто — «без причины».</param>
    /// <param name="bannedByNick">
    /// Ник того, кто забанил. Он попадает в запись и в объявление игрокам — владелец
    /// просил, чтобы было видно, что бан не «от сервера», а от конкретного человека.
    /// </param>
    /// <param name="error">Текст отказа для страницы или <c>null</c> при успехе.</param>
    public BanRecord? BanPlayerById(
        int playerId, string? duration, string? reason, string? bannedByNick, out string? error)
    {
        error = null;

        if (_bans is null)
        {
            error = "баны не инициализированы";
            return null;
        }

        var settings = _config.Admin.Ban;
        if (!settings.Enabled)
        {
            error = "баны выключены (admin.ban.enabled = false)";
            return null;
        }

        var seen = _directory.Resolve(playerId);
        if (seen is null || string.IsNullOrWhiteSpace(seen.Nick))
        {
            error = $"id #{playerId} сервер не знает — человек ещё не заходил в лобби";
            return null;
        }

        // Те же правила, что у /ban: сервер не банится, админ не банится, себя нельзя.
        if (ServerIdentity.IsServerNick(seen.Nick))
        {
            error = $"'{ServerIdentity.Nick}' — это сам сервер, его забанить нельзя";
            return null;
        }

        if (_config.IsAdmin(MatchPublicId.ForNick(seen.Nick)))
        {
            error = $"id #{playerId} — админ (admin.ids), админов банить нельзя";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(bannedByNick)
            && string.Equals(
                ServerConfig.NormalizeNick(bannedByNick),
                ServerConfig.NormalizeNick(seen.Nick),
                StringComparison.OrdinalIgnoreCase))
        {
            error = "себя банить нельзя";
            return null;
        }

        if (!BanDuration.TryParse(
                duration,
                TimeSpan.FromMinutes(settings.MinMinutes),
                TimeSpan.FromDays(settings.MaxDays),
                out var span,
                out var durationError))
        {
            error = durationError;
            return null;
        }

        // Адрес цели: из справочника (все, с которых заходил) и из живой сессии, если
        // человек сейчас в лобби — иначе бан по IP не сработает до переподключения.
        var live = _session.JoinedPlayers()
            .FirstOrDefault(p => string.Equals(
                ServerConfig.NormalizeNick(p.Name),
                ServerConfig.NormalizeNick(seen.Nick),
                StringComparison.OrdinalIgnoreCase));

        var address = live?.Peer.Address;
        var record = _bans.Add(
            new BanProbe(seen.Nick, address?.ToString()),
            span,
            string.IsNullOrWhiteSpace(reason) ? "без причины" : reason,
            string.IsNullOrWhiteSpace(bannedByNick) ? LobbySession.ServerPlayerName : bannedByNick.Trim(),
            DateTime.UtcNow,
            playerId,
            seen.Ips);

        // Кик из матча и из лобби — разные сокеты, нужны оба; это тот же путь, что у /ban.
        var matchKicked = _match.DisconnectBanned(seen.Nick, address);
        foreach (var target in _session.JoinedPlayers())
        {
            if (!string.Equals(
                    ServerConfig.NormalizeNick(target.Name),
                    ServerConfig.NormalizeNick(seen.Nick),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try { target.Peer.Disconnect(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[ban] сайт: не смог отключить '{target.Name}': {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[ban] сайт: {record.DisplayNick} на {BanDuration.FormatShort(span)} причиной " +
            $"'{record.Reason}' (кто: {record.BannedBy}); id={playerId}; " +
            $"IP=[{string.Join(", ", record.Ips)}]; кик матч={matchKicked}");

        // Игроки должны видеть, что это бан от человека (владелец просил именно так) —
        // текст берётся из того же BanMessages, что и у команды в чате.
        if (settings.NotifyEveryone)
        {
            foreach (var line in BanMessages.PublicLines(record))
                BroadcastServerChat(line);
        }

        return record;
    }

    /// <summary>
    /// Снять баны, выданные под этим id. Тот же путь, что у <c>/unban &lt;id&gt;</c>.
    /// </summary>
    /// <param name="removed">Сколько записей снято.</param>
    /// <param name="error">Текст отказа или <c>null</c>.</param>
    public bool UnbanPlayerById(int playerId, out int removed, out string? error)
    {
        removed = 0;
        error = null;

        if (_bans is null)
        {
            error = "баны не инициализированы";
            return false;
        }

        var hits = _bans.RemoveByMatchId(playerId);
        if (hits.Count == 0)
        {
            error = $"под id #{playerId} активных банов нет";
            return false;
        }

        removed = hits.Count;
        foreach (var record in hits)
            BroadcastServerChat($"разбанен {record.DisplayNick}");

        Console.WriteLine($"[ban] сайт: /unban {playerId}: снято {hits.Count} записей");
        return true;
    }

    /// <summary>Адрес игрока, если он сейчас в лобби; иначе <c>null</c>.</summary>
    public IPAddress? LobbyAddressOf(int playerId)
    {
        foreach (var player in _session.JoinedPlayers())
        {
            if (MatchPublicId.ForNick(player.Name) == playerId)
                return player.Peer.Address;
        }

        return null;
    }
}
