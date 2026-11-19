using System.Globalization;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Reports;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Поиск игроков для сайта: превью жалобы, публичный профиль и список администрации.
/// </summary>
/// <remarks>
/// <para>
/// Ищем по числовому ID или по кастомному ID. Кастомный имеет приоритет: именно его
/// игрок видит в игре. Ник и устройства берутся у сервера, а не из тела запроса.
/// </para>
/// <para>
/// Список администрации собирается из живого <c>admin.ids</c>, а ник — из
/// <see cref="PlayerDirectory"/>. Если админ ещё ни разу не заходил, остаётся
/// честная строка «ID …» без выдуманного ника.
/// </para>
/// </remarks>
public sealed partial class GameNetHost
{
    /// <summary>
    /// Новые устройства игрока: жалобы дописывают их к старым записям, чтобы смена
    /// ника не обнуляла историю. Подписывается веб-панель.
    /// </summary>
    internal Action<int, IReadOnlyList<string>>? PlayerDevicesSeen { get; set; }

    /// <summary>Найти цель жалобы по числовому или кастомному ID. Нет такого — null.</summary>
    internal ReportTarget? LookupReportTarget(string? query)
    {
        var text = (query ?? "").Trim();
        if (text.Length == 0)
            return null;

        int? id = _customIds.FindPlayerByCustomId(text);
        if (id is null
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)
            && numeric > 0)
        {
            id = numeric;
        }

        // Ник в ссылке (#/profile/vlal): справочник помнит ники всех, кого видел.
        if (id is null)
            id = _directory.FindByNick(text)?.Id;

        return id is null ? null : DescribeReportTarget(id.Value);
    }

    /// <summary>
    /// Все админы из живого конфига. Ник может быть пустым — это не ошибка:
    /// админ просто ещё не заходил после установки сервера.
    /// </summary>
    internal IReadOnlyList<ReportTarget> AdministrationForWeb()
    {
        var ids = _config.Admin.Ids.ToArray();
        var result = new List<ReportTarget>(ids.Length);
        var seen = new HashSet<int>();

        foreach (var id in ids)
        {
            if (!seen.Add(id))
                continue;

            var target = DescribeReportTarget(id);
            if (target is not null)
            {
                result.Add(target);
                continue;
            }

            // Конфиг провалидирован, но страховка дешёвая: страница не должна падать
            // из-за одной кривой записи в admin.ids.
            var custom = CustomIdOf(id);
            result.Add(new ReportTarget(
                id, "", custom, custom ?? id.ToString(CultureInfo.InvariantCulture),
                true, "offline", false, false, null,
                Array.Empty<string>(), null));
        }

        result.Sort((a, b) => a.Id.CompareTo(b.Id));
        return result;
    }

    /// <summary>
    /// Описание игрока по числовому ID: ник, где он сейчас, кастомный ID, устройства.
    /// Не-админа, которого сервер никогда не видел, не выдумываем — null.
    /// </summary>
    private ReportTarget? DescribeReportTarget(int id)
    {
        if (id < AdminConfig.MinId || id > AdminConfig.MaxId)
            return null;

        var found = LookupPlayerForAdmin(id);
        var custom = CustomIdOf(id);
        var display = custom ?? id.ToString(CultureInfo.InvariantCulture);
        var devices = _directory.DevicesForId(id);

        if (found is null)
        {
            // Игрок офлайн, но справочник его помнит: профиль обязан открываться и без
            // онлайна — иначе ссылка из истории матчей вела бы в «не найден».
            var seen = _directory.Resolve(id);
            if (seen is not null && !string.IsNullOrWhiteSpace(seen.Nick))
            {
                DateTime? lastSeen = seen.LastSeenUtcMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(seen.LastSeenUtcMs).UtcDateTime
                    : null;
                return new ReportTarget(
                    id, seen.Nick, custom, display, _config.IsAdmin(id), "offline",
                    false, IsPlayerBannedById(id), lastSeen, devices, null);
            }

            if (!_config.IsAdmin(id))
                return null;

            return new ReportTarget(
                id, "", custom, display, true, "offline", false, false, null,
                devices, null);
        }

        return new ReportTarget(
            found.Id,
            found.Nick,
            custom,
            display,
            _config.IsAdmin(found.Id),
            found.Status,
            found.HasAvatar,
            found.Banned,
            found.LastSeenUtc,
            devices,
            found.Note);
    }
}
