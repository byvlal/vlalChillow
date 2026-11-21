using System.Text.Json;
using System.Text.Json.Serialization;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Bans;

/// <summary>
/// Аккаунт игрока: постоянный номер, который сервер выдаёт при первом входе.
/// </summary>
/// <remarks>
/// Номер не зависит от ника: ник — только подпись, которую можно сменить. Устройства
/// (<c>LB&lt;md5&gt;</c> из 2.07-handshake) привязываются к аккаунту при входе в катку и
/// переживают смену ника и VPN. Пока устройство не привязано, аккаунт считается
/// «лёгким»: по нему нельзя банить чужой новый вход, иначе невинный, взявший ник
/// забаненного, унаследовал бы чужой бан.
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

    public bool HasNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return false;

        return AllNicks.Any(n => string.Equals(n, normalized, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>Текущая схема файла.</summary>
    public const int CurrentVersion = 1;

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
    // Текущий ник и прежние ники — разные индексы. Иначе алиас одного аккаунта
    // перекрывает действующий ник другого и тот перестаёт находиться по своему же нику.
    private readonly Dictionary<string, Account> _byNick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Account> _byAlias = new(StringComparer.OrdinalIgnoreCase);
    // Одно устройство может доказать несколько аккаунтов (разные ники на одной установке).
    // Список, а не один аккаунт: иначе второй аккаунт вытесняет первый из проверки банов.
    private readonly Dictionary<string, List<Account>> _byDevice = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId = FirstId;
    private bool _readOnly;

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

    public Account? FindById(int id)
    {
        if (id < FirstId || id > MaxId)
            return null;

        lock (_gate)
            return _byId.TryGetValue(id, out var account) ? account : null;
    }

    /// <summary>Аккаунт по нику (текущему или прежнему). Регистр не важен.</summary>
    /// <remarks>
    /// Действующий ник всегда важнее прежнего: если два аккаунта делят строку, побеждает
    /// тот, для кого она сейчас <see cref="Account.Nick"/>, а не тот, у кого она осталась
    /// алиасом. Так алиас не может «украсть» чужой действующий ник.
    /// </remarks>
    public Account? FindByNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return null;

        lock (_gate)
            return FindByNickLocked(normalized);
    }

    /// <summary>Ник под замком: сначала действующий владелец, затем самый свежий алиас.</summary>
    private Account? FindByNickLocked(string normalized)
    {
        if (_byNick.TryGetValue(normalized, out var account))
            return account;

        return _byAlias.TryGetValue(normalized, out var alias) ? alias : null;
    }

    /// <summary>
    /// Все аккаунты, которым принадлежит ник (текущий или прежний). Нужно проверкам
    /// админа: тёзка под тем же ником не должен отобрать права у настоящего владельца.
    /// </summary>
    public IReadOnlyList<Account> FindAccountsByNick(string? nick)
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

    /// <summary>Есть ли у устройства уже доказанный аккаунт с таким ником.</summary>
    private static Account? DeviceAccountWithNickLocked(
        List<Account> accounts, string normalizedNick)
    {
        // Действующий ник важнее алиаса — как и в FindByNickLocked.
        foreach (var account in accounts)
        {
            if (string.Equals(account.Nick, normalizedNick, StringComparison.OrdinalIgnoreCase))
                return account;
        }

        foreach (var account in accounts)
        {
            if (account.Aliases.Any(a => string.Equals(
                    a, normalizedNick, StringComparison.OrdinalIgnoreCase)))
                return account;
        }

        return null;
    }

    /// <summary>
    /// Аккаунт для успешного входа в лобби: ник уже доказан, устройство ещё нет.
    /// Создаёт новый номер только если такого ника сервер раньше не видел.
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
    /// Аккаунт для входа в катку. Личность доказывается ником и устройством:
    /// <list type="number">
    /// <item>у устройства уже есть аккаунт с этим ником (сейчас или в алиасах) — он;</item>
    /// <item>ник свободен (аккаунт без устройств, например только что из лобби) — занять его;</item>
    /// <item>ник уже занят чужим устройством — новый аккаунт, чтобы тёзка не наследовал бан;</item>
    /// <item>ник новый — новый аккаунт.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Одно устройство может доказать несколько аккаунтов: смена ника на той же установке
    /// больше не переименовывает старый аккаунт, а заводит рядом новый. Старый остаётся
    /// со своим номером и находится по старому нику — номер не переиспользуется и не
    /// «исчезает» при создании второго аккаунта.
    /// </para>
    /// <para>
    /// Пустой <paramref name="device"/> (legacy-handshake) не создаёт новый аккаунт на
    /// каждый реконнект: без устройства личность доказывается только ником, поэтому
    /// переиспользуем существующую запись.
    /// </para>
    /// </remarks>
    public Account? EnsureForJoin(string? nick, string? device, DateTime nowUtc)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return null;

        var normalizedDevice = BanRecord.NormalizeDevice(device);

        lock (_gate)
        {
            // 1. Это устройство уже владеет аккаунтом с таким ником (текущим или прежним).
            //    Сильнее любого другого совпадения: возврат на старый ник находит старый
            //    аккаунт, даже если кто-то другой успел взять ту же строку.
            if (normalizedDevice.Length > 0
                && _byDevice.TryGetValue(normalizedDevice, out var deviceAccounts)
                && DeviceAccountWithNickLocked(deviceAccounts, normalized) is { } byDeviceNick)
            {
                TouchLocked(byDeviceNick, normalized, normalizedDevice, nowUtc);
                return byDeviceNick;
            }

            // 2. Ник уже известен. Аккаунт без устройств можно занять (это тот же человек,
            //    который только что вошёл в лобби); с этим же устройством — тот же человек;
            //    с чужим устройством — новый номер, иначе тёзка наследует чужой бан.
            if (FindByNickLocked(normalized) is { } byNick
                && (normalizedDevice.Length == 0
                    || byNick.Devices.Count == 0
                    || byNick.HasDevice(normalizedDevice)))
            {
                TouchLocked(byNick, normalized, normalizedDevice, nowUtc);
                return byNick;
            }

            // 3. Новый ник или занятый чужим устройством — отдельный постоянный номер.
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
    /// Перевести старый хеш ника в аккаунт. Порядок: ник записи (самый надёжный
    /// признак), затем реальный account ID (старый хеш мог случайно попасть в
    /// диапазон новых номеров), затем скан по <see cref="LegacyNickHash"/> всех
    /// ников и алиасов. <c>0</c> — перевести не удалось.
    /// </summary>
    public int MapLegacyId(int oldId, string? nick)
    {
        if (FindByNick(nick) is { } byNick)
            return byNick.Id;

        if (oldId >= FirstId && oldId <= MaxId && FindById(oldId) is { } byId)
            return byId.Id;

        var byHash = FindAccountIdsByLegacyId(oldId);
        return byHash.Count > 0 ? byHash[0] : 0;
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
                    Version = CurrentVersion,
                    NextId = _nextId,
                    Accounts = _accounts.OrderBy(a => a.Id).ToList(),
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
        if (normalizedNick.Length > 0)
        {
            if (!string.Equals(account.Nick, normalizedNick, StringComparison.Ordinal))
            {
                var oldNick = account.Nick;
                if (oldNick.Length > 0)
                {
                    // Старый действующий ник становится прежним: он должен продолжать
                    // находиться, но не перекрывать чужой действующий ник.
                    if (!account.Aliases.Any(a => string.Equals(
                            a, oldNick, StringComparison.OrdinalIgnoreCase)))
                    {
                        account.Aliases.Add(oldNick);
                    }

                    if (_byNick.TryGetValue(oldNick, out var owner)
                        && ReferenceEquals(owner, account))
                    {
                        _byNick.Remove(oldNick);
                    }
                    _byAlias[oldNick] = account;
                }

                account.Nick = normalizedNick;
                account.Aliases.RemoveAll(a => string.Equals(
                    a, normalizedNick, StringComparison.OrdinalIgnoreCase));
                changed = true;
            }

            _byNick[normalizedNick] = account;
            _byAlias.Remove(normalizedNick);
            foreach (var alias in account.Aliases)
            {
                if (alias.Length > 0)
                    _byAlias[alias] = account;
            }
        }

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

            if (dto.Version > CurrentVersion)
            {
                _readOnly = true;
                Console.WriteLine(
                    $"[account] {_path}: версия {dto.Version} новее поддерживаемой " +
                    $"{CurrentVersion} — читаю, но сохранять не буду");
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
                _nextId = FirstId;
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

    private sealed class FileDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("nextId")]
        public int NextId { get; set; } = FirstId;

        [JsonPropertyName("accounts")]
        public List<Account>? Accounts { get; set; } = new();
    }
}
