using System.Text.RegularExpressions;

namespace StandChillow.LanServer.Bans;

/// <summary>
/// Переопределения публичного id: админ назначил себе (или другому id) буквенно-цифровой
/// публичный id вместо числового.
/// </summary>
/// <remarks>
/// <para>
/// Серверный id — это чистая функция от ника (<see cref="MatchPublicId"/>), и он остаётся
/// ключом всех проверок: баны, <c>admin.ids</c>, <c>/ids</c>, <c>uid</c> в катке. Здесь
/// хранится только <b>то, что видит человек</b>: в игре, в чате и на сайте вместо
/// <c>25354325</c> показывается выбранная строка.
/// </para>
/// <para>
/// Поэтому подмена не может дать прав и не может спрятать человека от бана: и то, и
/// другое считается по числовому id из ника. Записи живут до перезапуска сервера —
/// ключ от <c>auth.txt</c> тоже живёт один запуск, и пережившая его подмена выглядела бы
/// как «сервер забыл ничей id».
/// </para>
/// </remarks>
public sealed class PublicIdOverrides
{
    /// <summary>Предел длины: короче 2 символов смысла нет, длиннее — не влезает в UI.</summary>
    public const int MinLength = 2;
    public const int MaxLength = 16;

    /// <summary>
    /// Разрешены латиница и цифры, без спецсимволов и пробелов: строка показывается в
    /// игровом списке игроков и в чате, и любой разделитель там только мешает.
    /// </summary>
    private static readonly Regex Allowed = new("^[A-Za-z0-9]+$", RegexOptions.Compiled);

    private readonly object _gate = new();

    /// <summary>Числовой id человека → выбранная им строка.</summary>
    private readonly Dictionary<int, string> _byId = new();

    /// <summary>Выбранная строка (в нижнем регистре) → чей это id: одна строка — один человек.</summary>
    private readonly Dictionary<string, int> _byPublic = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Проверить и нормализовать выбранный публичный id.
    /// </summary>
    /// <remarks>
    /// Только латиница и цифры: кириллица и спецсимволы запрещены намеренно — строка
    /// попадает в игровой список и в чат, где «#» и «@» уже заняты разметкой команд.
    /// </remarks>
    public static bool TryNormalize(string? raw, out string value, out string error)
    {
        value = (raw ?? string.Empty).Trim();
        error = string.Empty;

        if (value.Length < MinLength)
        {
            error = $"публичный id короче {MinLength} символов";
            return false;
        }

        if (value.Length > MaxLength)
        {
            error = $"публичный id длиннее {MaxLength} символов";
            return false;
        }

        if (!Allowed.IsMatch(value))
        {
            error = "в публичном id только латинские буквы и цифры, без пробелов и спецсимволов";
            return false;
        }

        // Целиком из цифр — это чужой числовой id: «занять» его нельзя, иначе два человека
        // в игре выглядели бы одинаково.
        if (value.All(char.IsAsciiDigit))
        {
            error = "публичный id не может состоять только из цифр — это чужие числовые id";
            return false;
        }

        return true;
    }

    /// <summary>Что показать вместо числового id: подмена или сам числовой id.</summary>
    public string DisplayOf(int id)
    {
        lock (_gate)
            return _byId.TryGetValue(id, out var custom) ? custom : id.ToString();
    }

    /// <summary>Публичный id этого человека (для сайта и чата).</summary>
    public string? CustomOf(int id)
    {
        lock (_gate)
            return _byId.TryGetValue(id, out var custom) ? custom : null;
    }

    /// <summary>
    /// Назначить публичный id. Проверки те же, что у команды: чужая занятая строка не
    /// отбирается, занять числовой id нельзя.
    /// </summary>
    public bool TrySet(int id, string? raw, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;

        if (id <= 0)
        {
            error = "нужен игрок с настоящим id";
            return false;
        }

        if (!TryNormalize(raw, out value, out error))
            return false;

        lock (_gate)
        {
            if (_byPublic.TryGetValue(value, out var owner) && owner != id)
            {
                error = $"публичный id '{value}' уже занят другим игроком";
                return false;
            }

            // Освободить прошлую строку этого же человека, чтобы он мог её переиспользовать.
            if (_byId.TryGetValue(id, out var previous))
                _byPublic.Remove(previous);

            _byId[id] = value;
            _byPublic[value] = id;
            return true;
        }
    }

    /// <summary>Снять подмену: человек снова показывается числовым id.</summary>
    public bool Clear(int id)
    {
        lock (_gate)
        {
            if (!_byId.Remove(id, out var previous))
                return false;
            _byPublic.Remove(previous);
            return true;
        }
    }

    /// <summary>Сколько подмен сейчас живо.</summary>
    public int Count
    {
        get { lock (_gate) return _byId.Count; }
    }

    /// <summary>Что показать в логе: подмену, если она есть, иначе числовой id.</summary>
    public string Describe(int id)
    {
        lock (_gate)
            return _byId.TryGetValue(id, out var custom) ? custom : id.ToString();
    }
}
