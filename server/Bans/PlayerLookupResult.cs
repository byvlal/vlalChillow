namespace StandChillow.LanServer.Bans;

/// <summary>
/// Результат поиска игрока по серверному id — для превью в веб-админке (вкладка «Баны»).
/// </summary>
/// <param name="Id">Серверный id (<see cref="MatchPublicId.ForNick"/>).</param>
/// <param name="Nick">Ник (как его знает сервер: в катке — ростер, офлайн — справочник).</param>
/// <param name="Status">
/// Где человек сейчас: <c>match</c> — в катке, <c>lobby</c> — в лобби, <c>offline</c> — не в сети.
/// </param>
/// <param name="HasAvatar">Есть ли аватар в лобби (байты, присланные клиентом при входе).</param>
/// <param name="Banned">Активный ли на него бан прямо сейчас.</param>
/// <param name="LastSeenUtc">Когда последний раз замечен (только для офлайна).</param>
/// <param name="CustomId">Кастомный ID, если админ его поставил (иначе null).</param>
/// <param name="Note">
/// Предупреждение: коллизия id (несколько ников делят одно число) — по такому id банить
/// сервер откажется, и админ увидит это заранее, а не после «Забанить».
/// </param>
public sealed record PlayerLookupResult(
    int Id,
    string Nick,
    string Status,
    bool HasAvatar,
    bool Banned,
    DateTime? LastSeenUtc,
    string? CustomId,
    string? Note);
