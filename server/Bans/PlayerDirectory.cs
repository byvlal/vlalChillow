using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Bans;

/// <summary>Кого сервер когда-либо видел: id аккаунта, ник, адреса, устройства, когда заходил.</summary>
/// <remarks>
/// Это память, а не бан: нужна, чтобы <c>/ban</c> находил человека, который уже вышел
/// из катки или вообще отключился. Хранится в <c>banlist.json</c> рядом с банами.
/// </remarks>
public sealed class SeenPlayer
{
    public int Id { get; set; }
    public string Nick { get; set; } = "";
    public List<string> Ips { get; set; } = new();
    public List<string> Devices { get; set; } = new();
    public long LastSeenUtcMs { get; set; }
}

/// <summary>
/// Постоянный справочник «аккаунт ↔ ник ↔ IP», переживающий рестарт сервера.
/// </summary>
/// <remarks>
/// <para>
/// Id приходит из <see cref="AccountStore"/>: это постоянный номер аккаунта, а не хеш
/// ника. Ключ записи — аккаунт: два разных человека с одинаковым ником остаются двумя
/// записями, поэтому <c>/ban</c> по id не задевает невинного тёзку. Ник — только подпись;
/// индекс по нику указывает на самую свежую запись.
/// </para>
/// <para>
/// Ограничение размера: последние <see cref="MaxEntries"/> личностей. Старые вытесняются,
/// чтобы файл не рос бесконечно на публичном сервере. Сам аккаунт при этом не удаляется:
/// номера не переиспользуются.
/// </para>
/// </remarks>
public sealed class PlayerDirectory
{
    public const int MaxEntries = 5000;

    private readonly object _gate = new();
    private readonly List<SeenPlayer> _players = new();
    // Ключ — номер аккаунта: один человек — одна запись, даже если ник менялся.
    private readonly Dictionary<int, SeenPlayer> _byId = new();
    // Ник — точная подпись аккаунта, с учётом регистра: «Влал» и «влал» — разные люди.
    // После склейки дублей на один точный ник приходится один аккаунт; индекс оставлен
    // для ссылок вида #/profile/<ник> и указывает на самую свежую запись.
    private readonly Dictionary<string, SeenPlayer> _byNick = new(StringComparer.Ordinal);

    /// <summary>Вызывается после изменения — сюда навешивается запись на диск.</summary>
    public Action? Changed { get; set; }

    public int Count
    {
        get { lock (_gate) return _players.Count; }
    }

    /// <summary>
    /// Запомнить вход: ник, id аккаунта, адрес и (если известен) ключ устройства.
    /// <paramref name="accountId"/> обязателен: без него запись не создаётся, чтобы
    /// теневой или забаненный вход не оставил в справочнике чужой адрес.
    /// </summary>
    public SeenPlayer? Remember(
        string? nick,
        string? ip,
        DateTime nowUtc,
        string? device = null,
        int accountId = 0)
    {
        if (accountId is < AccountStore.FirstId or > AccountStore.MaxId)
            return null;

        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return null;

        lock (_gate)
        {
            var changed = false;
            if (!_byId.TryGetValue(accountId, out var player))
            {
                player = new SeenPlayer { Id = accountId, Nick = normalized };
                _byId[accountId] = player;
                _players.Add(player);
                TrimLocked();
                changed = true;
            }
            else if (!string.Equals(player.Nick, normalized, StringComparison.Ordinal))
            {
                // Ник — подпись, а не ключ: аккаунт остаётся тем же.
                var oldNick = player.Nick;
                player.Nick = normalized;
                if (oldNick.Length > 0
                    && _byNick.TryGetValue(oldNick, out var mapped)
                    && ReferenceEquals(mapped, player))
                {
                    _byNick.Remove(oldNick);
                }
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(ip) && !player.Ips.Contains(ip))
            {
                player.Ips.Add(ip);
                changed = true;
            }

            var normalizedDevice = BanRecord.NormalizeDevice(device);
            if (normalizedDevice.Length > 0
                && !player.Devices.Any(d => string.Equals(
                    d, normalizedDevice, StringComparison.OrdinalIgnoreCase)))
            {
                player.Devices.Add(normalizedDevice);
                changed = true;
            }

            // Последний вариант написания — только для показа; запись та же.
            // Индекс по нику переводим на самую свежую запись: если ник делят два
            // аккаунта, ссылки вида #/profile/<ник> ведут к тому, кто заходил позже.
            _byNick[normalized] = player;

            // LastSeen обновляем только в памяти: писать файл на каждый вход — это I/O
            // в пути подключения. На диск попадёт при ближайшем бане/разбане.
            player.LastSeenUtcMs = BanRecord.ToUnixMs(nowUtc);

            if (changed)
                NotifyChanged();
            return player;
        }
    }

    /// <summary>
    /// Все устройства, которые сервер видел у этого аккаунта. Нужно банам и жалобам:
    /// при смене ника старые записи должны находиться по устройству, а не по числу.
    /// </summary>
    public IReadOnlyList<string> DevicesForAccount(int accountId)
    {
        if (accountId <= 0)
            return Array.Empty<string>();

        lock (_gate)
            return _byId.TryGetValue(accountId, out var player)
                ? player.Devices.ToList()
                : Array.Empty<string>();
    }

    /// <summary>Найти личность по id аккаунта.</summary>
    public SeenPlayer? Resolve(int id)
    {
        if (id <= 0)
            return null;

        lock (_gate)
            return _byId.TryGetValue(id, out var player) ? player : null;
    }

    /// <summary>
    /// Найти личность по точному нику (регистр важен). Побеждает самая свежая запись.
    /// </summary>
    /// <remarks>
    /// Нужно для ссылок вида <c>#/profile/vlal</c>: в URL можно писать ник, а не только
    /// цифровой ID. После склейки дублей точный ник ведёт на один аккаунт; если в старом
    /// файле остались две записи, берём ту, которая заходила позже.
    /// </remarks>
    public SeenPlayer? FindByNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return null;

        lock (_gate)
            return _byNick.TryGetValue(normalized, out var player) ? player : null;
    }

    /// <summary>
    /// Сколько записей делят этот id. После перехода на аккаунты всегда 0 или 1:
    /// оставлено для честных сообщений админу в старых путях.
    /// </summary>
    public int CountById(int id)
    {
        if (id <= 0)
            return 0;

        lock (_gate)
            return _byId.ContainsKey(id) ? 1 : 0;
    }

    public IReadOnlyList<SeenPlayer> Export()
    {
        lock (_gate)
            return _players.ToList();
    }

    /// <summary>
    /// Загрузить записи, в том числе старые с хешем ника вместо account ID.
    /// </summary>
    /// <remarks>
    /// Диапазон номера ничего не доказывает: старый хеш тоже бывает больше
    /// <see cref="AccountStore.FirstId"/>. Поэтому записи склеиваются только по
    /// совпавшему id, а старые хеши переводит на аккаунты <see cref="RemapAccountIds"/>.
    /// </remarks>
    public void Load(IEnumerable<SeenPlayer> players)
    {
        lock (_gate)
        {
            _players.Clear();
            _byId.Clear();
            _byNick.Clear();

            var byId = new Dictionary<int, SeenPlayer>();

            foreach (var source in players)
            {
                if (source is null || string.IsNullOrWhiteSpace(source.Nick))
                    continue;

                var player = new SeenPlayer
                {
                    Id = source.Id,
                    Nick = ServerConfig.NormalizeNick(source.Nick),
                    LastSeenUtcMs = source.LastSeenUtcMs,
                    Ips = (source.Ips ?? new List<string>())
                        .Where(ip => !string.IsNullOrWhiteSpace(ip))
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    Devices = (source.Devices ?? new List<string>())
                        .Select(BanRecord.NormalizeDevice)
                        .Where(device => device.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                };
                if (player.Nick.Length == 0)
                    continue;

                if (byId.TryGetValue(player.Id, out var existing))
                    MergeLocked(existing, player);
                else
                    byId[player.Id] = player;
            }

            _players.AddRange(byId.Values);
            RebuildIndexesLocked();
            TrimLocked();
        }
    }

    /// <summary>
    /// Перевести старые номера на аккаунты и склеить записи, попавшие на один аккаунт.
    /// <paramref name="map"/> получает старый id и ник записи, возвращает account ID
    /// (0 — запись выбросить: такого человека в реестре аккаунтов нет).
    /// </summary>
    /// <remarks>
    /// <paramref name="map"/> вызывается для каждой записи, а не только для «старых»
    /// номеров: старый хеш ника может выглядеть как номер аккаунта. Настоящий номер
    /// функция возвращает без изменений, поэтому повторная миграция ничего не портит.
    /// </remarks>
    public int RemapAccountIds(Func<int, string, int> map)
    {
        lock (_gate)
        {
            var changed = 0;
            var merged = new Dictionary<int, SeenPlayer>();

            foreach (var player in _players.OrderBy(p => p.LastSeenUtcMs).ThenBy(p => p.Id))
            {
                var oldId = player.Id;
                var mapped = oldId > 0 ? map(oldId, player.Nick) : 0;

                if (mapped is < AccountStore.FirstId or > AccountStore.MaxId)
                {
                    changed++;
                    continue;
                }

                if (mapped != oldId)
                {
                    player.Id = mapped;
                    changed++;
                }

                if (merged.TryGetValue(mapped, out var existing))
                {
                    MergeLocked(existing, player);
                    changed++;
                }
                else
                {
                    merged[mapped] = player;
                }
            }

            _players.Clear();
            _players.AddRange(merged.Values);
            RebuildIndexesLocked();
            TrimLocked();
            return changed;
        }
    }

    private static void MergeLocked(SeenPlayer target, SeenPlayer incoming)
    {
        // Номер аккаунта — личность и ключ словаря: его не перебивает более свежая
        // запись. Свежесть меняет только подпись (ник) и время последнего входа.
        if (incoming.LastSeenUtcMs > target.LastSeenUtcMs)
        {
            target.LastSeenUtcMs = incoming.LastSeenUtcMs;
            target.Nick = incoming.Nick;
        }

        foreach (var ip in incoming.Ips)
        {
            if (!target.Ips.Contains(ip))
                target.Ips.Add(ip);
        }

        foreach (var device in incoming.Devices)
        {
            if (!target.Devices.Any(d => string.Equals(
                    d, device, StringComparison.OrdinalIgnoreCase)))
                target.Devices.Add(device);
        }
    }

    private void RebuildIndexesLocked()
    {
        _byId.Clear();
        _byNick.Clear();

        // Сначала старые: последняя (самая свежая) запись с этим точным ником побеждает.
        foreach (var player in _players.OrderBy(p => p.LastSeenUtcMs).ThenBy(p => p.Id))
        {
            _byId[player.Id] = player;
            if (player.Nick.Length > 0)
                _byNick[player.Nick] = player;
        }
    }

    private void TrimLocked()
    {
        if (_players.Count <= MaxEntries)
            return;

        var drop = _players
            .OrderBy(p => p.LastSeenUtcMs)
            .Take(_players.Count - MaxEntries)
            .ToList();
        foreach (var player in drop)
        {
            _players.Remove(player);

            if (_byId.TryGetValue(player.Id, out var mapped) && ReferenceEquals(mapped, player))
                _byId.Remove(player.Id);

            if (player.Nick.Length > 0
                && _byNick.TryGetValue(player.Nick, out mapped)
                && ReferenceEquals(mapped, player))
            {
                _byNick.Remove(player.Nick);
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
            Console.WriteLine($"[ban] справочник: сохранить не удалось: {ex.Message}");
        }
    }
}
