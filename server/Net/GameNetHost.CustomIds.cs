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
/// </remarks>
public sealed partial class GameNetHost
{
    private readonly CustomIdStore _customIds =
        new(System.IO.Path.Combine(AppContext.BaseDirectory, "customids.json"));

    /// <summary>Хранилище кастомных ID — для веб-админки.</summary>
    public CustomIdStore CustomIds => _customIds;

    /// <summary>Кастомный ID игрока (null — показываем числовой).</summary>
    internal string? CustomIdOf(int id) => _customIds.Get(id);

    /// <summary>
    /// Поставить/сбросить кастомный ID админа. Пустая строка — сброс.
    /// Возвращает текст ошибки или <c>null</c> при успехе.
    /// </summary>
    internal string? SetCustomId(int id, string? value)
    {
        var nick = id > 0 ? _directory.Resolve(id)?.Nick : null;
        return _customIds.Set(id, nick, value);
    }
}
