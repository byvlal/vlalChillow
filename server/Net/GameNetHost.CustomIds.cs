namespace StandChillow.LanServer.Net;

/// <summary>
/// Кастомные ID админов: хранилище, выдача матчу и веб-панели.
/// </summary>
/// <remarks>
/// <para>
/// Хранилище на диске (<c>customids.json</c>) — рестарт ничего не теряет. Матч получает
/// делегат и подменяет числовой <c>uid</c> на кастомный в момент рассылки свойств, лобби —
/// в строке «Ты &lt;ник&gt; · ID: …». Оригинальный числовой ID остаётся источником правды
/// для банов, <c>admin.ids</c> и статистики.
/// </para>
/// <para>
/// Кастомный ID — привилегия админа, а не отдельная учётка. Права проверяются на каждом
/// чтении: убрали id из <c>admin.ids</c> — строка исчезает сразу, даже если файл ещё не
/// успели почистить. <see cref="PruneNonAdmins"/> досылает это состояние на диск и просит
/// матч разослать игрокам новый <c>uid</c>.
/// </para>
/// </remarks>
public sealed partial class GameNetHost
{
    private readonly CustomIdStore _customIds =
        new(System.IO.Path.Combine(AppContext.BaseDirectory, "customids.json"));

    /// <summary>Хранилище кастомных ID — для веб-админки.</summary>
    public CustomIdStore CustomIds => _customIds;

    /// <summary>Кастомный ID игрока (null — показываем числовой).</summary>
    /// <remarks>
    /// Не-админ кастомного ID не получает никогда, даже если запись осталась в файле:
    /// один источник правды — живой <c>admin.ids</c>, а не <c>customids.json</c>.
    /// </remarks>
    internal string? CustomIdOf(int id) => IsAdminId(id) ? _customIds.Get(id) : null;

    /// <summary>
    /// Поставить/сбросить кастомный ID админа. Пустая строка — сброс.
    /// Возвращает текст ошибки или <c>null</c> при успехе.
    /// </summary>
    internal string? SetCustomId(int id, string? value)
    {
        if (!IsAdminId(id))
            return "кастомный ID доступен только админам из admin.ids";

        var nick = id > 0 ? _directory.Resolve(id)?.Nick : null;
        var error = _customIds.Set(id, nick, value);
        if (error is null)
        {
            // Новый ID должен появиться у клиентов сразу, а не после перезахода:
            // матч разошлёт uid на ближайшем тике, если человек сейчас в катке.
            _match.RequestUidResync();
        }
        return error;
    }

    /// <summary>
    /// Снять кастомные ID у тех, кто перестал быть админом, и обновить uid в катках.
    /// </summary>
    /// <remarks>
    /// Вызывается при старте и на каждом применении конфига. Если кто-то потерял права
    /// посреди катки, клиенты уже видят его старую строку — матч разошлёт числовой id
    /// на ближайшем тике (флаг, без работы в чужом потоке).
    /// </remarks>
    private void PruneCustomIds()
    {
        var dropped = _customIds.PruneNonAdmins(_config.IsAdmin);
        if (dropped == 0)
            return;

        Console.WriteLine(
            $"[customid] снято у не-админов: {dropped} — кастомный ID доступен только admin.ids");
        _match.RequestUidResync();
    }
}
