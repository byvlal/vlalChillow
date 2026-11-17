using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Код входа на сайт: выдача в лобби и команда <c>/code</c> (#51).
/// </summary>
/// <remarks>
/// <para>
/// Код — единственный мост между лобби и сайтом. Он нужен, чтобы человек, создающий
/// комнату на сайте, был тем же человеком, что стоит в лобби: ник присылает клиент и
/// вписать можно любой, а код выдаёт сервер и знает его только сам игрок.
/// </para>
/// <para>
/// Провод не затронут: код уходит обычным server-сообщением лобби, ни одного нового
/// опкода или поля.
/// </para>
/// </remarks>
public sealed partial class GameNetHost
{
    /// <summary>
    /// Коды входа на сайт. Одно хранилище на запуск сервера: перезапуск гасит все коды и
    /// все сессии — авторизацию приходится проходить заново (#51).
    /// </summary>
    public SiteLoginCodes SiteCodes => _siteCodes;

    /// <summary>
    /// Строка «кто я»: ник, серверный ID, код для сайта и онлайн одной строкой.
    /// </summary>
    /// <remarks>
    /// Уходит ровно один раз и только самому игроку — и при входе в лобби, и по <c>/code</c>.
    /// Это замена прежней рассылке полного состава лобби: состав больше не уходит никому.
    /// </remarks>
    private void SendIdentityLine(ConnectedPlayer player)
    {
        var id = MatchPublicId.ForNick(player.Name);
        var code = _siteCodes.Issue(id, player.Name, DateTime.UtcNow);

        // Кастомный ID админа показываем вместо числа: игроки видят то же, что и в катке.
        // Оригинальный числовой ID остаётся источником правды для банов и admin.ids.
        var displayId = _customIds.Get(id) ?? id.ToString();

        var line = $"Ты {player.Name} · ID: {displayId}";
        if (code is not null)
            line += $" · код: {code}";
        line += $" · {BuildOnlineSummary()}";

        SendServerChatTo(player.Peer, line);

        // Подсказку — отдельной строкой и только когда код действительно выдан: без неё
        // цифры выглядят как часть ID, и человек ищет «свой id» там, где уже написан код.
        if (code is not null)
            SendServerChatTo(player.Peer, "код — для входа на сайт, живёт 30 минут · новый: /code");
    }

    /// <summary>
    /// Выход из лобби: код протухает сразу.
    /// </summary>
    /// <remarks>
    /// Только для настоящего выхода из лобби. Человек, ушедший в катку, остаётся «в игре»:
    /// его код живёт до конца срока (#51 — «дисконнектнулся с лобби (не с матча!)»).
    /// </remarks>
    private void ExpireSiteCodeFor(ConnectedPlayer player)
    {
        if (player.Status == PlayerLobbyStatus.InMatch)
            return;

        var id = MatchPublicId.ForNick(player.Name);
        if (id <= 0)
            return;

        _siteCodes.Expire(id);
    }

    /// <summary>
    /// <c>/code</c> — перевыпустить код для сайта. Старый умирает в ту же секунду.
    /// </summary>
    private void HandleCodeCommand(Action<string> reply, string who, ConnectedPlayer? player)
    {
        // У консоли нет своего кода: оператор и так хозяин, а код привязан к игроку лобби.
        if (player is null)
        {
            reply("код выдаётся игроку в лобби — из консоли его взять негде");
            return;
        }

        var id = MatchPublicId.ForNick(player.Name);
        var code = _siteCodes.Issue(id, player.Name, DateTime.UtcNow);
        if (code is null)
        {
            reply("не получилось выпустить код (пустой ник?) — перезайди в лобби");
            return;
        }

        Console.WriteLine($"[site-code] {who}: выпущен новый код для '{player.Name}' (ID {id})");
        reply($"твой код для сайта: {code} — живёт 30 минут, старый больше не работает");
    }
}
