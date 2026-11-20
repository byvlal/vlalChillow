using System.Security.Cryptography;

namespace StandChillow.LanServer.Net.Lobby;

/// <summary>
/// Одна запись кода: кто, когда получил и до какого времени жив.
/// </summary>
/// <param name="PlayerId">Серверный ID игрока (<see cref="LegacyNickHash.ForNick"/>).</param>
/// <param name="Nick">Ник на момент выдачи — сайт показывает его в «Привет, …».</param>
/// <param name="Code">Сам код, 6 цифр.</param>
/// <param name="IssuedUtc">Когда выдан.</param>
/// <param name="ExpiresUtc">Когда перестаёт действовать.</param>
public sealed record SiteLoginCode(
    int PlayerId,
    string Nick,
    string Code,
    DateTime IssuedUtc,
    DateTime ExpiresUtc);

/// <summary>
/// Коды входа на сайт: выдаются в лобби, живут полчаса, умирают при выходе из лобби.
/// </summary>
/// <remarks>
/// <para>
/// Правила — ровно те, что просил владелец (#51), и ни одного лишнего:
/// </para>
/// <list type="bullet">
/// <item><description>код выдаётся при входе в лобби, один на игрока;</description></item>
/// <item><description>новая выдача мгновенно убивает предыдущий код того же игрока;</description></item>
/// <item><description>код действует 30 минут, после чего не принимается;</description></item>
/// <item><description>выход из лобби (дисконнект именно с 7778) код протухает сразу;</description></item>
/// <item><description>переход в катку и выход из катки код <b>не</b> трогают;</description></item>
/// <item><description>рестарт сервера гасит всё: хранилище живёт только в памяти.</description></item>
/// </list>
/// <para>
/// Ключ — серверный ID игрока, а не ник: ID — та же производная от ника, что показывается
/// в строке «Ты … · ID: …» и в катке как <c>uid</c>, поэтому сайт и лобби говорят об одном
/// и том же человеке. IP здесь не участвует: браузер заходит не с того адреса, что игра.
/// </para>
/// <para>
/// Класс не знает ни о сокетах, ни о HTTP: его могут звать и сетевой поток лобби, и поток
/// веб-панели — поэтому всё под локом, а время передаётся аргументом (тестам так проще).
/// </para>
/// </remarks>
public sealed class SiteLoginCodes
{
    /// <summary>Код — 6 цифр. Нижняя граница отсекает ведущий ноль: «012345» наберут как «12345».</summary>
    public const int MinCode = 100_000;

    /// <summary>Верхняя граница (не включительно) — код строго из шести цифр.</summary>
    public const int MaxCodeExclusive = 1_000_000;

    /// <summary>Сколько живёт код, если конфиг ничего не сказал.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Dictionary<int, SiteLoginCode> _byPlayer = new();
    private readonly Dictionary<string, int> _byCode = new(StringComparer.Ordinal);

    /// <summary>
    /// Срок действия кода. Из <c>web.codeTtlSec</c> (горячая перезагрузка конфига),
    /// по умолчанию 30 минут.
    /// </summary>
    public TimeSpan Ttl { get; set; } = DefaultTtl;

    /// <summary>Сколько кодов сейчас активно.</summary>
    public int Count
    {
        get { lock (_gate) return _byPlayer.Count; }
    }

    /// <summary>
    /// Выдать игроку новый код. Предыдущий код этого игрока умирает в ту же секунду.
    /// </summary>
    /// <param name="playerId">Серверный ID; неположительный — код не выдаётся.</param>
    /// <param name="nick">Ник на момент выдачи.</param>
    /// <param name="nowUtc">Текущее время.</param>
    /// <returns>Код или <c>null</c>, если ID не годится.</returns>
    public string? Issue(int playerId, string nick, DateTime nowUtc)
    {
        if (playerId <= 0)
            return null;

        lock (_gate)
        {
            // Старый код — долой: «если код пересоздался — старый сразу удаляется и
            // невозможно его поюзать» (#51).
            if (_byPlayer.TryGetValue(playerId, out var previous))
            {
                _byCode.Remove(previous.Code);
                _byPlayer.Remove(playerId);
            }

            var ttl = Ttl > TimeSpan.Zero ? Ttl : DefaultTtl;
            var code = NewUniqueCode();
            var entry = new SiteLoginCode(playerId, nick, code, nowUtc, nowUtc + ttl);
            _byPlayer[playerId] = entry;
            _byCode[code] = playerId;
            return code;
        }
    }

    /// <summary>
    /// Активный код игрока, если он есть и не протух.
    /// </summary>
    public bool TryGet(int playerId, DateTime nowUtc, out SiteLoginCode? code)
    {
        code = null;
        if (playerId <= 0)
            return false;

        lock (_gate)
        {
            if (!_byPlayer.TryGetValue(playerId, out var entry))
                return false;

            if (entry.ExpiresUtc <= nowUtc)
            {
                // Протух — выбрасываем: держать мёртвую запись незачем, а «старый код
                // сразу удаляется» обязано выполняться и без явной ротации.
                _byCode.Remove(entry.Code);
                _byPlayer.Remove(playerId);
                return false;
            }

            code = entry;
            return true;
        }
    }

    /// <summary>
    /// Протушить код игрока немедленно — например, он вышел из лобби.
    /// </summary>
    public void Expire(int playerId)
    {
        if (playerId <= 0)
            return;

        lock (_gate)
        {
            if (!_byPlayer.Remove(playerId, out var entry))
                return;
            _byCode.Remove(entry.Code);
        }
    }

    /// <summary>
    /// Найти код, который ввёл человек на сайте. Только живой: протухший или несуществующий
    /// — одно и то же «код не существует или неактивен».
    /// </summary>
    /// <param name="code">Что ввёл человек; пробелы и мусор вокруг — не беда.</param>
    /// <param name="nowUtc">Текущее время.</param>
    /// <param name="entry">Найденная запись.</param>
    public bool TryFind(string? code, DateTime nowUtc, out SiteLoginCode? entry)
    {
        entry = null;
        var normalized = Normalize(code);
        if (normalized is null)
            return false;

        lock (_gate)
        {
            if (!_byCode.TryGetValue(normalized, out var playerId))
                return false;
            if (!_byPlayer.TryGetValue(playerId, out var found))
            {
                // Рассинхронизация словарей — такого быть не должно, но молча принять
                // чужой код страшнее, чем честно отказать.
                return false;
            }

            if (found.ExpiresUtc <= nowUtc)
            {
                _byCode.Remove(found.Code);
                _byPlayer.Remove(playerId);
                return false;
            }

            entry = found;
            return true;
        }
    }

    /// <summary>Выбросить протухшие записи. Возвращает, сколько выбросили.</summary>
    public int Prune(DateTime nowUtc)
    {
        List<SiteLoginCode>? dead = null;
        lock (_gate)
        {
            foreach (var entry in _byPlayer.Values)
            {
                if (entry.ExpiresUtc > nowUtc) continue;
                (dead ??= new List<SiteLoginCode>()).Add(entry);
            }

            if (dead is null)
                return 0;

            foreach (var entry in dead)
            {
                _byPlayer.Remove(entry.PlayerId);
                _byCode.Remove(entry.Code);
            }
        }

        return dead?.Count ?? 0;
    }

    /// <summary>
    /// Привести ввод к виду «шесть цифр или ничего». Пусто, не цифры, не та длина — не код.
    /// </summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        var text = code.Trim();
        if (text.Length != 6)
            return null;

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
                return null;
        }

        return text;
    }

    /// <summary>
    /// Свежий код, которого нет среди активных. Криптографический генератор: код — это
    /// вход на сайт, угадывать его не должны.
    /// </summary>
    private string NewUniqueCode()
    {
        // Активных кодов столько же, сколько людей онлайн, — место в 900 000 вариантов
        // всегда есть. На всякий случай не крутимся бесконечно.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var value = RandomNumberGenerator.GetInt32(MinCode, MaxCodeExclusive);
            var text = value.ToString();
            if (!_byCode.ContainsKey(text))
                return text;
        }

        throw new InvalidOperationException("свободных кодов не осталось");
    }
}
