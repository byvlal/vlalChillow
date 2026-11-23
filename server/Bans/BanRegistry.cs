using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Bans;

/// <summary>Что спросили у реестра на входе: ник, адрес, устройство и публичный id.</summary>
/// <param name="Nick">Ник из пакета входа; на коннекте в матч его ещё нет.</param>
/// <param name="Ip">Любой адрес: LAN, Hamachi, VPN, публичный. Фильтра по типу нет.</param>
/// <param name="Device">
/// Стабильный ключ 2.07-установки (<c>LB&lt;md5&gt;</c>). На коннекте и в лобби его нет —
/// только в handshake канала матча.
/// </param>
/// <param name="PublicId">
/// Публичный id аккаунта (<see cref="AccountStore.FirstId"/> и выше). Передаётся только
/// тогда, когда устройство действительно принадлежит аккаунту: иначе новый человек под
/// чужим ником получил бы чужой бан. <c>0</c> — ключ в проверке не участвует.
/// </param>
public readonly record struct BanProbe(
    string? Nick,
    string? Ip,
    string? Device = null,
    int PublicId = 0);

/// <summary>По какому ключу сработал бан.</summary>
public enum BanMatchKind
{
    None,
    UserId,
    Ip,
    PublicId,
    Nick,
}

/// <summary>
/// Ответ реестра. <see cref="Banned"/> — не пускать; <see cref="AdminExempt"/> — это админ,
/// на него бан не распространяется (пишем в лог, а не молчим).
/// </summary>
public sealed class BanDecision
{
    public static readonly BanDecision Allowed = new();

    public bool Banned { get; init; }
    public bool AdminExempt { get; init; }
    public string MatchedBy { get; init; } = "";
    public BanMatchKind MatchKind { get; init; }
    public bool LearnedAlias { get; init; }
    public BanRecord? Record { get; init; }
}

/// <summary>
/// Реестр банов: userId ↔ IP ↔ ник, с обучением только по жёсткому совпадению.
/// </summary>
/// <remarks>
/// <para>
/// Жёсткие ключи — <c>userId</c> (<c>LB&lt;md5&gt;</c>), IP и публичный id аккаунта.
/// Публичный id приходит только вместе с устройством, которому он принадлежит, поэтому
/// не может «приклеиться» к невинному тёзке. Совпал любой жёсткий ключ — баним и
/// дописываем в запись новый ник и остальные ключи. Совпал только ник — баним эту
/// попытку и <b>ничего не учим</b>: иначе невинный, взявший чужой ник, навсегда
/// унаследует чужой бан.
/// </para>
/// <para>
/// IP — второй жёсткий ключ: совпал адрес — баним и учим новый ник и userId. Это
/// ловит «сменил ник и переустановился, но остался на том же адресе». Ник — мягкий
/// ключ: он только блокирует попытку и ничего не тянет за собой.
/// </para>
/// <para>
/// Всё под одним <c>lock</c>: проверка на входе — один словарный lookup, без скана
/// списка и без диска. Вызывается на подключении, не на каждом пакете.
/// </para>
/// </remarks>
public sealed class BanRegistry
{
    private readonly object _gate = new();
    private readonly List<BanRecord> _records = new();
    private readonly Dictionary<string, BanRecord> _byNick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BanRecord> _byIp = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BanRecord> _byDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, BanRecord> _byPublicId = new();

    /// <summary>Вызывается после изменения набора — сюда навешивается запись на диск.</summary>
    public Action? Changed { get; set; }

    /// <summary>
    /// Учить новые ники/IP/id при жёстком совпадении. Выключатель
    /// <c>admin.ban.bindAliases</c>: баны продолжают работать, но запись не растёт.
    /// </summary>
    public bool LearnAliases { get; set; } = true;

    /// <summary>
    /// Проверять и запоминать устройства. Выключатель <c>admin.ban.byDevice</c>:
    /// старые записи с устройствами остаются в файле, но вход по ним не блокируется.
    /// </summary>
    public bool ByDevice { get; set; } = true;

    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    /// <summary>
    /// Проверка на входе. <see cref="BanDecision.Banned"/> = false значит «пускать».
    /// </summary>
    /// <param name="isAdmin">Предикат «это админский id»: на админов бан не распространяется.</param>
    /// <param name="learn">
    /// Учить новые ключи при совпадении. Для чтения с сайта и теневых проверок — false:
    /// запрос не должен приклеивать чужой ник/IP к записи.
    /// </param>
    public BanDecision Inspect(
        BanProbe probe,
        DateTime nowUtc,
        Func<string?, bool>? isAdmin = null,
        bool learn = true)
    {
        // Канон адреса — до замка и до всех словарей: иначе один и тот же телефон
        // с IPv4 и IPv4-mapped IPv6 выглядит как два разных человека.
        var normalized = new BanProbe(
            probe.Nick,
            BanRecord.NormalizeIp(probe.Ip),
            probe.Device,
            probe.PublicId);

        lock (_gate)
        {
            var pruned = PruneLocked(nowUtc);
            if (pruned > 0)
                NotifyChanged();

            if (isAdmin is not null && isAdmin(normalized.Nick))
                return new BanDecision { AdminExempt = true, MatchedBy = "админ" };

            var record = FindLocked(normalized, out var matchKind);
            if (record is null)
                return BanDecision.Allowed;

            var learned = learn && LearnAliases && LearnLocked(record, normalized, matchKind);
            if (learned)
                NotifyChanged();

            return new BanDecision
            {
                Banned = true,
                Record = record,
                MatchedBy = Describe(matchKind),
                MatchKind = matchKind,
                LearnedAlias = learned,
            };
        }
    }

    /// <summary>Быстрая проверка только по IP — для гейта на коннекте, где ника ещё нет.</summary>
    public bool IsIpBanned(string? ip, DateTime nowUtc)
    {
        var normalized = BanRecord.NormalizeIp(ip);
        if (normalized.Length == 0)
            return false;

        lock (_gate)
            return _byIp.TryGetValue(normalized, out var record) && record.IsActive(nowUtc);
    }

    /// <summary>Найти активную запись по нику.</summary>
    public BanRecord? FindByNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return null;

        lock (_gate)
            return _byNick.TryGetValue(normalized, out var record) ? record : null;
    }

    /// <summary>Выдать бан. Существующая запись для этого userId/ника/IP продлевается, а не дублируется.</summary>
    /// <param name="matchId">
    /// Публичный id, если бан выдан по нему: тег нужен для <c>/unban</c>.
    /// </param>
    /// <param name="knownDevices">
    /// Все известные устройства личности (из живого матча или справочника). Дописываются
    /// в запись, чтобы <c>/ban</c> по вышедшему игроку сразу закрывал и его установки.
    /// </param>
    public BanRecord Add(
        BanProbe probe,
        TimeSpan duration,
        string reason,
        string bannedBy,
        DateTime nowUtc,
        int? matchId = null,
        IReadOnlyList<string>? knownIps = null,
        IReadOnlyList<string>? knownDevices = null)
    {
        var nick = ServerConfig.NormalizeNick(probe.Nick);
        var device = BanRecord.NormalizeDevice(probe.Device);
        var ip = BanRecord.NormalizeIp(probe.Ip);
        if (nick.Length == 0
            && ip.Length == 0
            && (!ByDevice || device.Length == 0))
            throw new ArgumentException("нужен ник, IP или устройство", nameof(probe));

        lock (_gate)
        {
            PruneLocked(nowUtc);

            BanRecord? record = null;
            if (ByDevice && device.Length > 0 && _byDevice.TryGetValue(device, out var byDevice))
                record = byDevice;
            else if (ip.Length > 0 && _byIp.TryGetValue(ip, out var byIp))
                record = byIp;
            else if (probe.PublicId != 0 && _byPublicId.TryGetValue(probe.PublicId, out var byId))
                record = byId;
            else if (nick.Length > 0 && _byNick.TryGetValue(nick, out var byNick))
                record = byNick;

            if (record is null)
            {
                record = new BanRecord
                {
                    Nick = nick,
                    CreatedUtcMs = BanRecord.ToUnixMs(nowUtc),
                    UtcOffsetMinutes = BanRecord.CurrentUtcOffsetMinutes(),
                    CreatedLocalIso = BanRecord.LocalIsoNow(),
                };
                _records.Add(record);
            }

            // Номер бана нужен сразу: его видят игрок в уведомлении, админ в ответе
            // и журнал при откате. У записей из старого файла он появляется при Load.
            record.EnsureBanId();

            if (nick.Length > 0)
            {
                if (string.IsNullOrEmpty(record.Nick))
                    record.Nick = nick;
                else if (!record.HasNick(nick))
                    record.Aliases.Add(nick);
                _byNick[nick] = record;
            }

            if (ip.Length > 0)
            {
                if (!record.HasIp(ip))
                    record.Ips.Add(ip);
                _byIp[ip] = record;
            }

            if (ByDevice && device.Length > 0)
            {
                if (!record.HasDevice(device))
                    record.Devices.Add(device);
                _byDevice[device] = record;
            }

            // Бан по id для уже вышедшего игрока: подтягиваем все его известные адреса,
            // иначе «бан по id» не закрыл бы ему вход с того же IP.
            if (knownIps is not null)
            {
                foreach (var known in knownIps)
                {
                    var normalizedIp = BanRecord.NormalizeIp(known);
                    if (normalizedIp.Length == 0 || record.HasIp(normalizedIp))
                        continue;
                    record.Ips.Add(normalizedIp);
                    _byIp[normalizedIp] = record;
                }
            }

            if (ByDevice && knownDevices is not null)
            {
                foreach (var known in knownDevices)
                {
                    var normalized = BanRecord.NormalizeDevice(known);
                    if (normalized.Length == 0 || record.HasDevice(normalized))
                        continue;
                    record.Devices.Add(normalized);
                    _byDevice[normalized] = record;
                }
            }

            // Теги id: запрошенный (если банили по id) и id аккаунта, если он известен.
            // Оба нужны /unban, даже если запись потом выросла новыми никами.
            if (matchId is { } requested && requested != 0)
                IndexPublicIdLocked(record, requested);

            if (probe.PublicId != 0)
                IndexPublicIdLocked(record, probe.PublicId);

            record.Reason = reason.Trim();
            record.BannedBy = string.IsNullOrWhiteSpace(bannedBy) ? "Server" : bannedBy.Trim();
            record.DurationSeconds = (int)Math.Clamp(duration.TotalSeconds, 0, int.MaxValue);
            record.ExpiresUtcMs = BanRecord.ToUnixMs(nowUtc.Add(duration));

            NotifyChanged();
            return record;
        }
    }

    /// <summary>
    /// Перевести теги <see cref="BanRecord.MatchIds"/> со старых хешей ника на аккаунты.
    /// Сам бан остаётся на нике/IP/устройстве; меняется только число для <c>/unban</c>.
    /// Возвращает число изменённых записей.
    /// </summary>
    public int RemapAccountIds(Func<int, string, int> map)
    {
        lock (_gate)
        {
            var changed = 0;
            foreach (var record in _records)
            {
                var next = new List<int>();
                foreach (var oldId in record.MatchIds)
                {
                    var mapped = oldId > 0 ? map(oldId, record.Nick) : 0;

                    if (mapped is < AccountStore.FirstId or > AccountStore.MaxId)
                    {
                        changed++;
                        continue;
                    }

                    if (!next.Contains(mapped))
                        next.Add(mapped);
                    if (mapped != oldId)
                        changed++;
                }

                if (next.Count != record.MatchIds.Count
                    || next.Where((id, i) => id != record.MatchIds[i]).Any())
                {
                    record.MatchIds = next;
                }
            }

            _byPublicId.Clear();
            foreach (var record in _records)
            foreach (var id in record.MatchIds)
                _byPublicId[id] = record;

            if (changed > 0)
                NotifyChanged();
            return changed;
        }
    }

    /// <summary>Снять бан по нику, IP или ключу устройства. Возвращает снятую запись.</summary>
    public bool Remove(string? nickOrIp, out BanRecord? removed)
    {
        removed = null;
        var token = (nickOrIp ?? string.Empty).Trim();
        if (token.Length == 0)
            return false;

        lock (_gate)
        {
            var nick = ServerConfig.NormalizeNick(token);
            var device = BanRecord.NormalizeDevice(token);
            var ip = BanRecord.NormalizeIp(token);
            var found = nick.Length > 0 && _byNick.TryGetValue(nick, out removed);
            if (!found && device.Length > 0)
                found = _byDevice.TryGetValue(device, out removed);
            if (!found && (ip.Length == 0 || !_byIp.TryGetValue(ip, out removed)))
                return false;

            if (removed is null)
                return false;

            DropLocked(removed, notify: true);
            return true;
        }
    }

    /// <summary>Снять все баны, выданные под публичным id (для <c>/unban</c>).</summary>
    public List<BanRecord> RemoveByMatchId(int matchId)
    {
        lock (_gate)
        {
            var hits = _records.Where(r => r.HasMatchId(matchId)).ToList();
            foreach (var record in hits)
                DropLocked(record, notify: false);

            if (hits.Count > 0)
                NotifyChanged();
            return hits;
        }
    }

    /// <summary>Удалить истёкшие записи. Возвращает, сколько удалено.</summary>
    public int Prune(DateTime nowUtc)
    {
        lock (_gate)
        {
            var removed = PruneLocked(nowUtc);
            if (removed > 0)
                NotifyChanged();
            return removed;
        }
    }

    /// <summary>Активные баны: ближайший к концу — первым.</summary>
    public IReadOnlyList<BanRecord> Active(DateTime nowUtc)
    {
        lock (_gate)
        {
            var removed = PruneLocked(nowUtc);
            if (removed > 0)
                NotifyChanged();

            return _records
                .Where(r => r.IsActive(nowUtc))
                .OrderBy(r => r.ExpiresUtcMs)
                .ToList();
        }
    }

    /// <summary>Снимок для записи на диск (копия списка, сами записи общие).</summary>
    public IReadOnlyList<BanRecord> Export()
    {
        lock (_gate)
            return _records.ToList();
    }

    /// <summary>Заполнить реестр прочитанными с диска записями (истёкшие сразу отбрасываются).</summary>
    public void Load(IEnumerable<BanRecord> records)
    {
        lock (_gate)
        {
            _records.Clear();
            _byNick.Clear();
            _byIp.Clear();
            _byDevice.Clear();
            _byPublicId.Clear();

            foreach (var record in records)
            {
                if (record is null)
                    continue;

                record.Ips ??= new List<string>();
                record.Aliases ??= new List<string>();
                record.MatchIds ??= new List<int>();
                record.Devices ??= new List<string>();
                record.Nick = ServerConfig.NormalizeNick(record.Nick);
                if (record.Nick.Length == 0)
                    record.Nick = record.Ips.FirstOrDefault() ?? "?";
                if (record.ExpiresUtcMs <= 0)
                    continue;

                record.Devices = record.Devices
                    .Select(BanRecord.NormalizeDevice)
                    .Where(device => device.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                record.Ips = record.Ips
                    .Select(BanRecord.NormalizeIp)
                    .Where(ip => ip.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                // Номер бана появляется у записей, созданных до v5. Он должен пережить
                // перезапуск: иначе журнал и сообщения будут ссылаться на разные номера.
                record.EnsureBanId();

                record.MatchIds = record.MatchIds
                    .Where(id => id != 0)
                    .Distinct()
                    .ToList();

                _records.Add(record);
                _byNick[record.Nick] = record;

                foreach (var alias in record.Aliases)
                {
                    var normalized = ServerConfig.NormalizeNick(alias);
                    if (normalized.Length > 0)
                        _byNick[normalized] = record;
                }

                foreach (var ip in record.Ips)
                {
                    if (!string.IsNullOrWhiteSpace(ip))
                        _byIp[ip] = record;
                }

                foreach (var device in record.Devices)
                    _byDevice[device] = record;

                foreach (var publicId in record.MatchIds)
                    _byPublicId[publicId] = record;
            }

            PruneLocked(DateTime.UtcNow);
        }
    }

    private BanRecord? FindLocked(BanProbe probe, out BanMatchKind matchKind)
    {
        matchKind = BanMatchKind.None;

        // userId — самый сильный ключ: не зависит ни от ника, ни от IP.
        if (ByDevice)
        {
            var device = BanRecord.NormalizeDevice(probe.Device);
            if (device.Length > 0 && _byDevice.TryGetValue(device, out var byDevice))
            {
                matchKind = BanMatchKind.UserId;
                return byDevice;
            }
        }

        // IP — второй жёсткий ключ. Раньше ника: новый ник на старом адресе должен
        // учиться, а не проходить как «мягкое» совпадение по нику.
        var ip = BanRecord.NormalizeIp(probe.Ip);
        if (ip.Length > 0 && _byIp.TryGetValue(ip, out var byIp))
        {
            matchKind = BanMatchKind.Ip;
            return byIp;
        }

        // Публичный id приходит только с устройством, которому он принадлежит
        // (см. AccountStore.ProbeId), поэтому это жёсткий ключ, а не догадка по нику.
        if (probe.PublicId != 0 && _byPublicId.TryGetValue(probe.PublicId, out var byPublicId))
        {
            matchKind = BanMatchKind.PublicId;
            return byPublicId;
        }

        var nick = ServerConfig.NormalizeNick(probe.Nick);
        if (nick.Length > 0 && _byNick.TryGetValue(nick, out var byNick))
        {
            matchKind = BanMatchKind.Nick;
            return byNick;
        }

        return null;
    }

    /// <summary>
    /// Дописать в запись новые ключи. Учим только по жёсткому совпадению:
    /// userId или IP. Совпадение по нику не учит ничего: это защита невинных тёзок.
    /// </summary>
    private bool LearnLocked(BanRecord record, BanProbe probe, BanMatchKind matchKind)
    {
        if (matchKind is BanMatchKind.None or BanMatchKind.Nick)
            return false;

        var learned = false;

        var nick = ServerConfig.NormalizeNick(probe.Nick);
        if (nick.Length > 0 && !record.HasNick(nick))
        {
            record.Aliases.Add(nick);
            _byNick[nick] = record;
            learned = true;
        }

        if (!string.IsNullOrWhiteSpace(probe.Ip) && !record.HasIp(probe.Ip))
        {
            var ip = BanRecord.NormalizeIp(probe.Ip);
            if (ip.Length > 0)
            {
                record.Ips.Add(ip);
                _byIp[ip] = record;
                learned = true;
            }
        }

        // Устройство учим при совпадении по IP или публичному id: при userId оно уже
        // совпало, при нике — это был бы обход через чужой ник.
        if (ByDevice && matchKind is BanMatchKind.Ip or BanMatchKind.PublicId)
        {
            var device = BanRecord.NormalizeDevice(probe.Device);
            if (device.Length > 0 && !record.HasDevice(device))
            {
                record.Devices.Add(device);
                _byDevice[device] = record;
                learned = true;
            }
        }

        if (probe.PublicId != 0 && !record.MatchIds.Contains(probe.PublicId))
        {
            record.MatchIds.Add(probe.PublicId);
            _byPublicId[probe.PublicId] = record;
            learned = true;
        }

        return learned;
    }

    private static string Describe(BanMatchKind kind) => kind switch
    {
        BanMatchKind.UserId => "userId",
        BanMatchKind.Ip => "IP",
        BanMatchKind.PublicId => "id",
        BanMatchKind.Nick => "ник",
        _ => "",
    };

    private int PruneLocked(DateTime nowUtc)
    {
        var nowMs = BanRecord.ToUnixMs(nowUtc);
        var expired = _records.Where(r => r.ExpiresUtcMs <= nowMs).ToList();
        foreach (var record in expired)
            DropLocked(record, notify: false);
        return expired.Count;
    }

    private void DropLocked(BanRecord record, bool notify)
    {
        _records.Remove(record);
        DropIndex(_byNick, record);
        DropIndex(_byIp, record);
        DropIndex(_byDevice, record);
        DropIndex(_byPublicId, record);
        if (notify)
            NotifyChanged();
    }

    private static void DropIndex(Dictionary<string, BanRecord> index, BanRecord record)
    {
        foreach (var key in index.Where(kv => ReferenceEquals(kv.Value, record)).Select(kv => kv.Key).ToList())
            index.Remove(key);
    }

    private static void DropIndex(Dictionary<int, BanRecord> index, BanRecord record)
    {
        foreach (var key in index.Where(kv => ReferenceEquals(kv.Value, record)).Select(kv => kv.Key).ToList())
            index.Remove(key);
    }

    private void IndexPublicIdLocked(BanRecord record, int publicId)
    {
        if (publicId == 0)
            return;

        if (!record.MatchIds.Contains(publicId))
            record.MatchIds.Add(publicId);
        _byPublicId[publicId] = record;
    }

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ban] сохранить изменения не удалось: {ex.Message}");
        }
    }
}
