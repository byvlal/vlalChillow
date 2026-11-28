using System.Text.Json;
using System.Text.Json.Serialization;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Bans;

/// <summary>
/// Аккаунт игрока: постоянный номер, который сервер выдаёт при первом входе.
/// </summary>
/// <remarks>
/// Номер не зависит от устройства: личность — точный ник (с учётом регистра).
/// Устройства (<c>LB&lt;md5&gt;</c> из 2.07-handshake) только записываются как улика
/// для банов, жалоб и логов; они больше не решают, тот это человек или нет.
/// Один точный ник — один аккаунт на любом устройстве. Любое изменение буквы или
/// регистра — уже другой аккаунт.
/// </remarks>
public sealed class Account
{
    /// <summary>Постоянный номер аккаунта, начиная с <see cref="AccountStore.FirstId"/>.</summary>
    public int Id { get; set; }

    /// <summary>Текущий (последний) ник аккаунта.</summary>
    public string Nick { get; set; } = "";

    /// <summary>Прежние ники этого аккаунта — для поиска и разбана по старой записи.</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>Стабильные ключи 2.07-установок, которые заходили под этим аккаунтом.</summary>
    public List<string> Devices { get; set; } = new();

    public long CreatedUtcMs { get; set; }
    public long LastSeenUtcMs { get; set; }

    [JsonIgnore]
    public IEnumerable<string> AllNicks
    {
        get
        {
            if (Nick.Length > 0)
                yield return Nick;
            foreach (var alias in Aliases)
                if (alias.Length > 0)
                    yield return alias;
        }
    }

    /// <summary>
    /// Совпадает ли ник с текущим или прежним. Регистр не важен — это поиск по
    /// истории для админки и банов, а не проверка личности.
    /// </summary>
    public bool HasNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return false;

        return AllNicks.Any(n => string.Equals(n, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Совпадает ли ник с текущим <b>точно</b>, с учётом регистра. Только это
    /// выражение является личностью аккаунта.
    /// </summary>
    public bool HasCurrentNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        return normalized.Length > 0
               && string.Equals(Nick, normalized, StringComparison.Ordinal);
    }

    public bool HasDevice(string? device)
    {
        var normalized = BanRecord.NormalizeDevice(device);
        return normalized.Length > 0
               && Devices.Any(d => string.Equals(
                   BanRecord.NormalizeDevice(d), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public void Normalize()
    {
        Nick = ServerConfig.NormalizeNick(Nick);
        Aliases ??= new List<string>();
        Devices ??= new List<string>();

        Aliases = Aliases
            .Select(ServerConfig.NormalizeNick)
            .Where(a => a.Length > 0
                        && !string.Equals(a, Nick, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Devices = Devices
            .Select(BanRecord.NormalizeDevice)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>Итог разовой перенумерации: карта старый→новый и сколько номеров реально изменилось.</summary>
public sealed class RenumberOutcome
{
    public static readonly RenumberOutcome None = new();

    public IReadOnlyDictionary<int, int> Map { get; init; } = new Dictionary<int, int>();
    public int Changed { get; init; }
    public bool Applied { get; init; }
}

/// <summary>
/// Постоянный реестр аккаунтов: <c>accounts.json</c> рядом с конфигом.
/// </summary>
/// <remarks>
/// <para>
/// Номера выдаются только вверх: первый успешный вход получает <see cref="FirstId"/>
/// (1 000 000), следующий — 1 000 001, и так далее. Номер никогда не переиспользуется,
/// даже если запись удалили: <c>admin.ids</c>, баны и статистика не должны внезапно
/// указывать на другого человека.
/// </para>
/// <para>
/// Разовая перенумерация v1 → v2 — исключение из «только вверх»: она уплотняет уже
/// выданные номера по первому входу, чтобы вернуть владельцу сервера его 1 000 000.
/// Старый → новый номер сохраняется в файле, пока все ссылки (справочник, баны,
/// статистика, история, жалобы, аватары) не переписаны; после этого карта удаляется.
/// </para>
/// <para>
/// Ник — не личность. Новый человек с тем же ником получает тот же аккаунт в лобби
/// (ник ещё ничего не доказывает), но при входе в катку с новым устройством ему
/// создаётся отдельный аккаунт. Поэтому ник забаненного не тянет чужой бан на невинного.
/// </para>
/// <para>
/// Запись атомарная (tmp → замена), битый файл не роняет сервер. Частые обновления
/// <see cref="Account.LastSeenUtcMs"/> остаются в памяти: на диск они попадут при
/// ближайшем структурном изменении (новый аккаунт, ник или устройство).
/// </para>
/// </remarks>
public sealed class AccountStore
{
    /// <summary>Первый выданный номер. Всё, что меньше, — старые хеши ников.</summary>
    public const int FirstId = 1_000_000;

    /// <summary>
    /// Верхняя граница номеров. Совпадает с диапазоном старого хеша
    /// (<see cref="LegacyNickHash.Max"/>): номер остаётся тем же «серверным id»,
    /// который проверяют баны, <c>admin.ids</c> и веб-панель.
    /// </summary>
    public const int MaxId = 100_000_000;

    /// <summary>Текущая схема файла: v2 — разовая перенумерация с сохранённой картой.</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Шаги, которые нужно переписать при перенумерации. Пока шаг не отмечен, его
    /// хранилище ещё может содержать старые номера; после отметки повторно не трогаем.
    /// </summary>
    public static readonly string[] RenumberSteps =
    {
        "directory",
        "custom",
        "bans",
        "stats",
        "history",
        "reports",
        "avatars",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<Account> _accounts = new();
    private readonly Dictionary<int, Account> _byId = new();
    // Текущий ник — ключ личности: сравнение точное, с учётом регистра.
    // «Skipp» и «skipp» — разные аккаунты, а не один.
    private readonly Dictionary<string, Account> _byNick = new(StringComparer.Ordinal);
    // Прежние ники — только история для админ-поиска. Личность по ним не находится.
    private readonly Dictionary<string, Account> _byAlias = new(StringComparer.OrdinalIgnoreCase);
    // Склеенные дубли: старый номер → канонический. Карта переживает рестарт,
    // чтобы статистика/история/аватары со старых номеров не потерялись.
    private readonly Dictionary<int, int> _mergedIds = new();
    // Одно устройство может доказать несколько аккаунтов (разные ники на одной установке).
    // Список, а не один аккаунт: иначе второй аккаунт вытесняет первый из проверки банов.
    private readonly Dictionary<string, List<Account>> _byDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> _renumberMap = new();
    private readonly HashSet<string> _renumberDone = new(StringComparer.Ordinal);
    private int _nextId = FirstId;
    private int _loadedVersion = 1;
    private bool _readOnly;
    private bool _renumberBlocked;

    public AccountStore(string path)
    {
        _path = path;
        Load();
        Console.WriteLine(
            $"[account] реестр: {_path}, аккаунтов {Count}, следующий номер {_nextId}");
    }

    /// <summary>Вызывается после структурного изменения — сюда навешивается запись на диск.</summary>
    public Action? Changed { get; set; }

    public string FilePath => _path;

    public int Count
    {
        get { lock (_gate) return _accounts.Count; }
    }

    /// <summary>Следующий номер, который получит новый аккаунт (для логов/тестов).</summary>
    public int NextId
    {
        get { lock (_gate) return _nextId; }
    }

    public bool IsReadOnly
    {
        get { lock (_gate) return _readOnly; }
    }

    /// <summary>Есть ли незавершённая перенумерация: карта старый→новый ещё нужна.</summary>
    public bool HasPendingRenumber
    {
        get { lock (_gate) return !_readOnly && _renumberMap.Count > 0; }
    }

    /// <summary>
    /// Файл ещё v1 и перенумерация не начиналась. Только в этом состоянии числовой
    /// литерал в <c>admin.ids</c> опасен: после миграции номера уже новые и трогать
    /// их не нужно.
    /// </summary>
    public bool NeedsRenumber
    {
        get
        {
            lock (_gate)
                return _loadedVersion < CurrentVersion && _renumberMap.Count == 0;
        }
    }

    /// <summary>Копия карты старый→новый (пусто, если перенумерация не нужна или уже завершена).</summary>
    public IReadOnlyDictionary<int, int> RenumberMap
    {
        get { lock (_gate) return new Dictionary<int, int>(_renumberMap); }
    }

    /// <summary>Все ли шаги перенумерации отмечены выполненными.</summary>
    public bool AllRenumberStepsDone
    {
        get
        {
            lock (_gate)
                return _renumberMap.Count > 0
                       && RenumberSteps.All(step => _renumberDone.Contains(step));
        }
    }

    /// <summary>Выполнен ли конкретный шаг перенумерации (файл уже переписан).</summary>
    public bool IsRenumberStepDone(string step)
    {
        lock (_gate)
            return _renumberDone.Contains(step);
    }

    /// <summary>
    /// Запретить перенумерацию в этом запуске: в <c>admin.ids</c> найден числовой ID,
    /// который нельзя безопасно переписать автоматически. Файл останется v1, следующий
    /// старт попробует снова после правки конфига.
    /// </summary>
    public void BlockRenumber(string reason)
    {
        lock (_gate)
        {
            if (_renumberBlocked)
                return;

            // Блокировать уже выполненную (или начатую) перенумерацию нельзя: файл
            // откатился бы на v1 и потерял сохранённую карту.
            if (_loadedVersion >= CurrentVersion || _renumberMap.Count > 0)
            {
                Console.WriteLine(
                    $"[account] перенумерация уже не нужна, блокировка пропущена: {reason}");
                return;
            }

            _renumberBlocked = true;
            Console.WriteLine($"[account] перенумерация пропущена: {reason}");
        }
    }

    /// <summary>
    /// Отметить шаг перенумерации выполненным и сразу сохранить карту, чтобы обрыв
    /// процесса не заставил переписывать этот файл ещё раз.
    /// </summary>
    public void MarkRenumberStepDone(string step)
    {
        lock (_gate)
        {
            if (_renumberMap.Count == 0 || !_renumberDone.Add(step))
                return;
            SaveLocked();
        }
    }

    /// <summary>
    /// Завершить перенумерацию: удалить карту из файла. Номера уже новые, старые
    /// ссылки больше не нужны.
    /// </summary>
    public void FinishRenumber()
    {
        lock (_gate)
        {
            if (_renumberMap.Count == 0)
                return;

            _renumberMap.Clear();
            _renumberDone.Clear();
            SaveLocked();
            Console.WriteLine("[account] перенумерация завершена: карта старый→новый удалена");
        }
    }

    /// <summary>Перевести старый номер по сохранённой карте. <c>0</c> — пары нет.</summary>
    public int MapRenumberedId(int oldId)
    {
        lock (_gate)
            return _renumberMap.TryGetValue(oldId, out var mapped) ? mapped : 0;
    }

    public Account? FindById(int id)
    {
        if (id < FirstId || id > MaxId)
            return null;

        lock (_gate)
            return _byId.TryGetValue(id, out var account) ? account : null;
    }

    /// <summary>
    /// Аккаунт по точному текущему нику. Регистр важен: «Skipp» и «skipp» —
    /// два разных аккаунта. Прежние ники личностью не считаются.
    /// </summary>
    public Account? FindByNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return null;

        lock (_gate)
            return FindByNickLocked(normalized);
    }

    /// <summary>Точный текущий ник под замком.</summary>
    private Account? FindByNickLocked(string normalized) =>
        _byNick.TryGetValue(normalized, out var account) ? account : null;

    /// <summary>
    /// Все аккаунты с этим точным текущим ником. После склейки дублей их не больше
    /// одного; список оставлен для страховки и старых файлов.
    /// </summary>
    public IReadOnlyList<Account> FindAccountsByNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return Array.Empty<Account>();

        lock (_gate)
        {
            return _accounts
                .Where(a => string.Equals(a.Nick, normalized, StringComparison.Ordinal))
                .OrderBy(a => a.Id)
                .ToList();
        }
    }

    /// <summary>
    /// Все аккаунты, у которых ник (текущий или прежний) совпадает без учёта
    /// регистра. Только для поиска в админке, не для личности и не для прав.
    /// </summary>
    public IReadOnlyList<Account> FindAccountsByAnyNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return Array.Empty<Account>();

        lock (_gate)
        {
            return _accounts
                .Where(a => a.AllNicks.Any(n => string.Equals(
                    n, normalized, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(a => a.Id)
                .ToList();
        }
    }

    /// <summary>
    /// Аккаунты, чей старый хеш ника равен <paramref name="legacyHash"/>. Скан, а не
    /// словарь: нужен только миграции, а ников в реестре немного.
    /// </summary>
    public IReadOnlyList<int> FindAccountIdsByLegacyId(int legacyHash)
    {
        if (legacyHash == 0)
            return Array.Empty<int>();

        lock (_gate)
        {
            return _accounts
                .Where(a => a.AllNicks.Any(n => LegacyNickHash.ForNick(n) == legacyHash))
                .Select(a => a.Id)
                .OrderBy(id => id)
                .ToList();
        }
    }

    /// <summary>
    /// Аккаунты, которые уже доказаны этим устройством. Один аккаунт — обычный случай;
    /// на одной установке их может быть несколько (разные ники/профили).
    /// </summary>
    public IReadOnlyList<Account> FindAccountsByDevice(string? device)
    {
        var normalized = BanRecord.NormalizeDevice(device);
        if (normalized.Length == 0)
            return Array.Empty<Account>();

        lock (_gate)
            return _byDevice.TryGetValue(normalized, out var accounts)
                ? accounts.OrderByDescending(a => a.LastSeenUtcMs).ThenByDescending(a => a.Id).ToList()
                : Array.Empty<Account>();
    }

    /// <summary>Самый свежий аккаунт устройства (для логов и старых вызовов).</summary>
    public Account? FindByDevice(string? device)
    {
        var accounts = FindAccountsByDevice(device);
        return accounts.Count > 0 ? accounts[0] : null;
    }

    /// <summary>
    /// Аккаунт для успешного входа в лобби. Ключ — точный ник; устройство в лобби
    /// ещё неизвестно. Создаёт новый номер только если такого точного ника нет.
    /// </summary>
    public Account? GetOrCreate(string? nick, DateTime nowUtc)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return null;

        lock (_gate)
        {
            if (FindByNickLocked(normalized) is { } existing)
            {
                TouchLocked(existing, normalized, device: null, nowUtc);
                return existing;
            }

            var account = NewLocked(normalized, device: null, nowUtc);
            NotifyChanged();
            return account;
        }
    }

    /// <summary>
    /// Аккаунт для входа в катку. Ключ — тот же точный ник, что и в лобби.
    /// Устройство только записывается в аккаунт как улика для бана; оно больше
    /// не создаёт новый номер и не отделяет «тёзку».
    /// </summary>
    public Account? EnsureForJoin(string? nick, string? device, DateTime nowUtc)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return null;

        var normalizedDevice = BanRecord.NormalizeDevice(device);

        lock (_gate)
        {
            if (FindByNickLocked(normalized) is { } existing)
            {
                TouchLocked(existing, normalized, normalizedDevice, nowUtc);
                return existing;
            }

            var account = NewLocked(normalized, normalizedDevice, nowUtc);
            NotifyChanged();
            return account;
        }
    }

    /// <summary>
    /// Все публичные id, которые доказаны устройством. Проверка бана смотрит каждый:
    /// на одной установке может быть несколько аккаунтов, и бан любого должен закрывать
    /// вход, даже если устройство сейчас представляется другим ником.
    /// </summary>
    public IReadOnlyList<int> ProbeIds(string? device)
    {
        return FindAccountsByDevice(device)
            .Select(a => a.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    /// <summary>
    /// Самый свежий публичный id устройства (для старых вызовов). Для проверки банов
    /// используйте <see cref="ProbeIds"/>: аккаунтов у устройства может быть несколько.
    /// </summary>
    public int ProbeId(string? device)
    {
        return FindByDevice(device)?.Id ?? 0;
    }

    /// <summary>
    /// Разовая склейка старых дублей: один точный ник — один аккаунт.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Канонический номер — админский, если в группе есть админ; иначе самый старый
    /// (по <see cref="Account.CreatedUtcMs"/>, затем по номеру). Устройства и прежние
    /// ники объединяются, статистика и остальные хранилища переводятся на канонический
    /// номер через <see cref="MapLegacyId"/>.
    /// </para>
    /// <para>
    /// Карта старый→канонический хранится в <c>accounts.json</c> постоянно: она нужна
    /// аватарам и старым ссылкам, у которых нет ника. Повторный вызов идемпотентен.
    /// </para>
    /// </remarks>
    /// <param name="isAdmin">Права по номеру: админский аккаунт не должен потерять номер.</param>
    /// <returns>Сколько аккаунтов склеено.</returns>
    public int MergeDuplicateNicks(Func<int, bool>? isAdmin = null)
    {
        lock (_gate)
        {
            var groups = _accounts
                .Where(a => a.Nick.Length > 0)
                .GroupBy(a => a.Nick, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToList();
            if (groups.Count == 0)
                return 0;

            var merged = 0;
            foreach (var group in groups)
            {
                var ordered = group
                    .OrderBy(a => isAdmin?.Invoke(a.Id) == true ? 0 : 1)
                    .ThenBy(a => a.CreatedUtcMs)
                    .ThenBy(a => a.Id)
                    .ToList();
                var canonical = ordered[0];

                foreach (var duplicate in ordered.Skip(1))
                {
                    if (duplicate.CreatedUtcMs > 0
                        && (canonical.CreatedUtcMs == 0
                            || duplicate.CreatedUtcMs < canonical.CreatedUtcMs))
                        canonical.CreatedUtcMs = duplicate.CreatedUtcMs;

                    if (duplicate.LastSeenUtcMs > canonical.LastSeenUtcMs)
                        canonical.LastSeenUtcMs = duplicate.LastSeenUtcMs;

                    foreach (var device in duplicate.Devices)
                    {
                        if (!canonical.HasDevice(device))
                            canonical.Devices.Add(device);
                    }

                    foreach (var alias in duplicate.Aliases)
                    {
                        if (alias.Length == 0
                            || string.Equals(alias, canonical.Nick, StringComparison.Ordinal)
                            || canonical.Aliases.Any(a => string.Equals(
                                a, alias, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        canonical.Aliases.Add(alias);
                    }

                    _mergedIds[duplicate.Id] = canonical.Id;
                    _accounts.Remove(duplicate);
                    _byId.Remove(duplicate.Id);
                    merged++;

                    Console.WriteLine(
                        $"[account] merge {duplicate.Id} → {canonical.Id} " +
                        $"('{canonical.Nick}'): devices={canonical.Devices.Count}, " +
                        $"aliases={canonical.Aliases.Count}");
                }

                canonical.Normalize();
            }

            RebuildIndexesLocked();
            BackupLocked("merge");
            SaveLocked();
            Console.WriteLine(
                $"[account] склейка дублей: {merged} аккаунтов, групп {groups.Count}, " +
                $"осталось {_accounts.Count}");
            return merged;
        }
    }

    /// <summary>Полностью пересобрать индексы после удаления/склейки записей.</summary>
    private void RebuildIndexesLocked()
    {
        _byId.Clear();
        _byNick.Clear();
        _byAlias.Clear();
        _byDevice.Clear();

        foreach (var account in _accounts)
        {
            _byId[account.Id] = account;
            if (account.Nick.Length > 0)
                _byNick[account.Nick] = account;
            foreach (var alias in account.Aliases)
            {
                if (alias.Length > 0)
                    _byAlias[alias] = account;
            }
            foreach (var device in account.Devices)
                BindDeviceLocked(device, account);
        }
    }

    /// <summary>Копия файла перед разрушительной миграцией. Ошибка не блокирует старт.</summary>
    private void BackupLocked(string reason)
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = $"{_path}.bak-{reason}-{stamp}";
            if (!File.Exists(backup))
                File.Copy(_path, backup);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[account] WARN бэкап не сделан: {ex.Message}");
        }
    }

    /// <summary>
    /// Дозаполнить реестр из старого справочника <c>banlist.json</c>. Аккаунты создаются
    /// в порядке первого появления (по <c>LastSeenUtcMs</c>), номера идут подряд с
    /// <see cref="FirstId"/>. Возвращает, сколько добавлено.
    /// </summary>
    public int SeedFromPlayers(IEnumerable<SeenPlayer> players)
    {
        var ordered = players
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Nick))
            .OrderBy(p => p.LastSeenUtcMs)
            .ThenBy(p => p.Id)
            .ThenBy(p => p.Nick, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var created = 0;
        lock (_gate)
        {
            foreach (var player in ordered)
            {
                var nick = ServerConfig.NormalizeNick(player.Nick);
                if (nick.Length == 0 || FindByNickLocked(nick) is not null)
                    continue;

                var seen = player.LastSeenUtcMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(player.LastSeenUtcMs).UtcDateTime
                    : DateTime.UtcNow;
                var account = NewLocked(nick, device: null, seen);
                foreach (var rawDevice in player.Devices ?? new List<string>())
                {
                    var device = BanRecord.NormalizeDevice(rawDevice);
                    if (device.Length == 0 || account.HasDevice(device))
                        continue;
                    account.Devices.Add(device);
                    BindDeviceLocked(device, account);
                }
                created++;
            }

            if (created > 0)
                NotifyChanged();
        }

        return created;
    }

    /// <summary>
    /// Разовая перенумерация v1 → v2: уплотнить номера с <see cref="FirstId"/> по
    /// порядку первого входа. Аккаунты из <paramref name="preferredFirst"/> (владелец
    /// и другие админы) идут первыми, чтобы вернуть владельцу его 1 000 000; остальные —
    /// по <see cref="Account.CreatedUtcMs"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Метод идемпотентен: на файле v2 без сохранённой карты он ничего не делает, а на
    /// файле с картой возвращает её же и не двигает номера повторно. Карта пишется в
    /// <c>accounts.json</c> вместе с новыми номерами и живёт до <see cref="FinishRenumber"/>.
    /// </para>
    /// <para>
    /// Если в <c>admin.ids</c> найден числовой ID, который нельзя безопасно переписать,
    /// вызывающий должен заранее позвать <see cref="BlockRenumber"/>: тогда файл
    /// останется v1 и следующий старт попробует снова.
    /// </para>
    /// </remarks>
    public RenumberOutcome RenumberIfNeeded(IReadOnlyCollection<int>? preferredFirst = null)
    {
        lock (_gate)
        {
            if (_readOnly || _renumberBlocked)
                return RenumberOutcome.None;

            // Уже есть сохранённая карта: номера новые, но не все файлы переписаны.
            // Возвращаем её, чтобы продолжить с того же места после рестарта.
            if (_renumberMap.Count > 0)
            {
                return new RenumberOutcome
                {
                    Map = new Dictionary<int, int>(_renumberMap),
                    Applied = false,
                };
            }

            if (_loadedVersion >= CurrentVersion)
                return RenumberOutcome.None;

            if (_accounts.Count == 0)
            {
                _loadedVersion = CurrentVersion;
                SaveLocked();
                return RenumberOutcome.None;
            }

            // Порядок админов важен: первый в списке (владелец) получает FirstId.
            // HashSet потерял бы его, поэтому храним номер позиции.
            var preferred = new Dictionary<int, int>();
            foreach (var id in preferredFirst ?? Array.Empty<int>())
            {
                if (!preferred.ContainsKey(id))
                    preferred[id] = preferred.Count;
            }

            var ordered = _accounts
                .OrderBy(a => preferred.TryGetValue(a.Id, out var rank) ? rank : int.MaxValue)
                .ThenBy(a => a.CreatedUtcMs)
                .ThenBy(a => a.Id)
                .ToList();

            var map = new Dictionary<int, int>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
                map[ordered[i].Id] = FirstId + i;

            var preferredCount = ordered.Count(a => preferred.ContainsKey(a.Id));
            var changed = map.Count(kv => kv.Key != kv.Value);
            foreach (var account in ordered)
                account.Id = map[account.Id];

            _accounts.Sort((a, b) => a.Id.CompareTo(b.Id));
            RebuildIdIndexLocked();
            _nextId = FirstId + _accounts.Count;
            _loadedVersion = CurrentVersion;
            _renumberDone.Clear();

            if (changed > 0)
            {
                _renumberMap.Clear();
                foreach (var (oldId, newId) in map)
                    _renumberMap[oldId] = newId;
            }

            SaveLocked();

            if (changed == 0)
            {
                Console.WriteLine(
                    $"[account] миграция v1→v2: номера уже идут подряд ({_accounts.Count}), " +
                    $"перенумерация не нужна");
                return RenumberOutcome.None;
            }

            Console.WriteLine(
                $"[account] миграция v1→v2: перенумеровано {changed} из {_accounts.Count} аккаунтов; " +
                $"первый {FirstId}, следующий {_nextId}; админских первыми: " +
                $"{preferredCount}");
            return new RenumberOutcome
            {
                Map = new Dictionary<int, int>(_renumberMap),
                Changed = changed,
                Applied = true,
            };
        }
    }

    /// <summary>
    /// Перевести старый хеш ника в аккаунт. Порядок: ник записи (самый надёжный
    /// признак), затем реальный account ID (старый хеш мог случайно попасть в
    /// диапазон новых номеров), затем скан по <see cref="LegacyNickHash"/> всех
    /// ников и алиасов. <c>0</c> — перевести не удалось.
    /// </summary>
    public int MapLegacyId(int oldId, string? nick)
    {
        // Склейка дублей: старый номер однозначно переводится на канонический,
        // даже если ник в записи пустой или уже сменился.
        lock (_gate)
        {
            if (_mergedIds.TryGetValue(oldId, out var merged))
                return merged;
        }

        if (FindByNick(nick) is { } byNick)
            return byNick.Id;

        if (oldId >= FirstId && oldId <= MaxId && FindById(oldId) is { } byId)
            return byId.Id;

        var byHash = FindAccountIdsByLegacyId(oldId);
        return byHash.Count > 0 ? byHash[0] : 0;
    }

    /// <summary>Перевести склеенный номер на канонический. <c>0</c> — пары нет.</summary>
    public int MapMergedId(int oldId)
    {
        lock (_gate)
            return _mergedIds.TryGetValue(oldId, out var mapped) ? mapped : 0;
    }

    /// <summary>Сколько пар склейки помнит реестр.</summary>
    public int MergedCount
    {
        get { lock (_gate) return _mergedIds.Count; }
    }

    public IReadOnlyList<Account> Export()
    {
        lock (_gate)
            return _accounts.OrderBy(a => a.Id).ToList();
    }

    /// <summary>Записать файл. Никогда не бросает — вызывающему нужен только факт.</summary>
    public bool Save()
    {
        lock (_gate)
            return SaveLocked();
    }

    /// <summary>
    /// Запись под уже взятым <c>_gate</c>. Версия 1, если перенумерация запрещена:
    /// иначе следующий старт решил бы, что миграция уже сделана, и не попробовал бы снова.
    /// </summary>
    private bool SaveLocked()
    {
        if (_readOnly)
        {
            Console.WriteLine(
                $"[account] {_path}: файл новее поддерживаемой схемы — сохранение пропущено");
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var dto = new FileDto
            {
                Version = _renumberBlocked ? 1 : CurrentVersion,
                NextId = _nextId,
                Accounts = _accounts.OrderBy(a => a.Id).ToList(),
                Merged = _mergedIds.Count > 0
                    ? _mergedIds
                        .OrderBy(kv => kv.Key)
                        .Select(kv => new RenumberEntry { Old = kv.Key, New = kv.Value })
                        .ToList()
                    : null,
                Renumber = _renumberMap.Count > 0
                    ? _renumberMap
                        .OrderBy(kv => kv.Key)
                        .Select(kv => new RenumberEntry { Old = kv.Key, New = kv.Value })
                        .ToList()
                    : null,
                RenumberDone = _renumberDone.Count > 0
                    ? _renumberDone.OrderBy(step => step, StringComparer.Ordinal).ToList()
                    : null,
            };
            var json = JsonSerializer.Serialize(dto, JsonOptions);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[account] не смог сохранить {_path}: {ex.Message}");
            return false;
        }
    }

    private Account NewLocked(string nick, string? device, DateTime nowUtc)
    {
        if (_nextId >= MaxId)
            throw new InvalidOperationException("аккаунты кончились");

        var nowMs = BanRecord.ToUnixMs(nowUtc);
        var account = new Account
        {
            Id = _nextId++,
            Nick = nick,
            CreatedUtcMs = nowMs,
            LastSeenUtcMs = nowMs,
        };

        var normalizedDevice = BanRecord.NormalizeDevice(device);
        if (normalizedDevice.Length > 0)
            account.Devices.Add(normalizedDevice);

        AddLocked(account);
        return account;
    }

    private void TouchLocked(Account account, string nick, string? device, DateTime nowUtc)
    {
        var changed = false;
        var normalizedNick = ServerConfig.NormalizeNick(nick);

        // Ник — это личность, а не подпись: вход не переименовывает аккаунт.
        // Если сюда пришёл другой ник, это уже другой аккаунт (его создаёт
        // GetOrCreate/EnsureForJoin), а не повод менять текущий.
        if (normalizedNick.Length > 0
            && !string.Equals(account.Nick, normalizedNick, StringComparison.Ordinal))
        {
            Console.WriteLine(
                $"[account] WARN вход под '{normalizedNick}' пришёл в аккаунт " +
                $"{account.Id} с ником '{account.Nick}' — ник не меняю");
        }

        if (account.Nick.Length > 0)
            _byNick[account.Nick] = account;

        var normalizedDevice = BanRecord.NormalizeDevice(device);
        if (normalizedDevice.Length > 0 && !account.HasDevice(normalizedDevice))
        {
            account.Devices.Add(normalizedDevice);
            BindDeviceLocked(normalizedDevice, account);
            changed = true;
        }

        var nowMs = BanRecord.ToUnixMs(nowUtc);
        if (nowMs > account.LastSeenUtcMs)
            account.LastSeenUtcMs = nowMs;

        if (changed)
            NotifyChanged();
    }

    private void AddLocked(Account account)
    {
        account.Normalize();
        if (account.Id < FirstId)
            account.Id = _nextId++;
        if (account.Id >= _nextId)
            _nextId = account.Id + 1;

        _accounts.Add(account);
        _byId[account.Id] = account;
        IndexNickLocked(account);

        foreach (var device in account.Devices)
            BindDeviceLocked(device, account);
    }

    private void IndexNickLocked(Account account)
    {
        if (account.Nick.Length > 0)
            _byNick[account.Nick] = account;

        foreach (var alias in account.Aliases)
        {
            if (alias.Length > 0)
                _byAlias[alias] = account;
        }
    }

    /// <summary>
    /// Привязать устройство к аккаунту, не отбирая его у других аккаунтов той же
    /// установки: один телефон может доказать несколько ников.
    /// </summary>
    private void BindDeviceLocked(string device, Account account)
    {
        if (!_byDevice.TryGetValue(device, out var accounts))
        {
            accounts = new List<Account>();
            _byDevice[device] = accounts;
        }

        if (!accounts.Any(a => a.Id == account.Id))
            accounts.Add(account);
    }

    /// <summary>После смены номеров: индекс по id должен указывать на новые ключи.</summary>
    private void RebuildIdIndexLocked()
    {
        _byId.Clear();
        foreach (var account in _accounts)
            _byId[account.Id] = account;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var dto = JsonSerializer.Deserialize<FileDto>(json, JsonOptions);
            if (dto?.Accounts is null)
                return;

            _loadedVersion = dto.Version > 0 ? dto.Version : 1;
            if (_loadedVersion > CurrentVersion)
            {
                _readOnly = true;
                Console.WriteLine(
                    $"[account] {_path}: версия {_loadedVersion} новее поддерживаемой " +
                    $"{CurrentVersion} — читаю, но сохранять не буду");
            }

            // Карта склейки дублей: старый номер → канонический. Живёт постоянно.
            foreach (var pair in dto.Merged ?? new List<RenumberEntry>())
            {
                if (pair.Old <= 0 || pair.New < FirstId || pair.New > MaxId
                    || pair.Old == pair.New)
                    continue;
                _mergedIds[pair.Old] = pair.New;
            }

            // Карта разовой перенумерации: нужна, пока не переписаны все хранилища.
            foreach (var pair in dto.Renumber ?? new List<RenumberEntry>())
            {
                if (pair.Old <= 0 || pair.New < FirstId || pair.New > MaxId)
                    continue;
                _renumberMap[pair.Old] = pair.New;
            }

            foreach (var step in dto.RenumberDone ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(step))
                    _renumberDone.Add(step);
            }

            // Порядок важен: _byNick должен указывать на самый свежий аккаунт с этим
            // ником. Сортируем по LastSeen, чтобы последний вход победил. Алиасы живут
            // отдельно и не могут перекрыть действующий ник другого аккаунта.
            foreach (var account in dto.Accounts
                         .Where(a => a is not null)
                         .OrderBy(a => a.LastSeenUtcMs)
                         .ThenBy(a => a.Id))
            {
                if (account.Id < FirstId || _byId.ContainsKey(account.Id))
                    continue;

                account.Normalize();
                if (account.Nick.Length == 0)
                    continue;

                AddLocked(account);
            }

            if (dto.NextId > _nextId)
                _nextId = dto.NextId;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[account] не смог прочитать {_path}: {ex.Message} — начинаю с пустого реестра");
            lock (_gate)
            {
                _accounts.Clear();
                _byId.Clear();
                _byNick.Clear();
                _byAlias.Clear();
                _byDevice.Clear();
                _mergedIds.Clear();
                _renumberMap.Clear();
                _renumberDone.Clear();
                _nextId = FirstId;
                _loadedVersion = 1;
                _renumberBlocked = false;
            }
        }
    }

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[account] сохранить изменения не удалось: {ex.Message}");
        }
    }

    /// <summary>Одна пара разовой перенумерации: было → стало.</summary>
    public sealed class RenumberEntry
    {
        [JsonPropertyName("old")]
        public int Old { get; set; }

        [JsonPropertyName("new")]
        public int New { get; set; }
    }

    private sealed class FileDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("nextId")]
        public int NextId { get; set; } = FirstId;

        [JsonPropertyName("accounts")]
        public List<Account>? Accounts { get; set; } = new();

        /// <summary>Склеенные дубли: старый номер → канонический. Живёт постоянно.</summary>
        [JsonPropertyName("merged")]
        public List<RenumberEntry>? Merged { get; set; }

        /// <summary>Карта старый→новый, пока перенумерация не завершена.</summary>
        [JsonPropertyName("renumber")]
        public List<RenumberEntry>? Renumber { get; set; }

        /// <summary>Какие хранилища уже переписаны на новые номера.</summary>
        [JsonPropertyName("renumberDone")]
        public List<string>? RenumberDone { get; set; }
    }
}
