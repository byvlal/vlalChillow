using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Баны на стороне лобби: команды, теневой вход, кик, разбан по сроку.
/// </summary>
/// <remarks>
/// Вход в лобби проверяется один раз (первый пакет): это один словарный lookup, без
/// диска и без блокировок в hot path. Команды проверяются отдельно — забаненный,
/// который успел войти до бана, не должен выполнять команды; ему повторяют полное
/// уведомление. Запись на диск случается лишь когда админ банит/разбанит или когда
/// жёсткий ключ (userId/IP) выучил новый ник или второй ключ.
/// </remarks>
public sealed partial class GameNetHost
{
    private readonly object _shadowGate = new();
    private readonly Dictionary<NetPeer, string> _shadowPeers = new();
    private readonly Dictionary<NetPeer, long> _shadowNoticeTicks = new();

    private BanRegistry? _bans;
    private readonly PlayerDirectory _directory = new();
    private BanStore? _banStore;
    private AccountStore? _accounts;
    private readonly object _legacyMapGate = new();
    private readonly Dictionary<int, int> _legacyAccountIds = new();
    private readonly object _adminIdsGate = new();
    private DateTime _lastBanSweepUtc = DateTime.MinValue;
    private DateTime _lastShadowRefreshUtc = DateTime.MinValue;

    /// <summary>Реестр банов (null — проба/тесты без банов).</summary>
    public BanRegistry? Bans => _bans;

    /// <summary>Реестр аккаунтов (null — проба/тесты без банов).</summary>
    public AccountStore? Accounts => _accounts;

    private void InitBans()
    {
        var settings = _config.Admin.Ban;
        var path = ResolveBanPath(settings.File);

        _banStore = new BanStore(path);
        _bans = new BanRegistry
        {
            LearnAliases = settings.BindAliases,
            ByDevice = settings.ByDevice,
        };

        var stored = _banStore.Load();
        _bans.Load(stored.Bans);
        _directory.Load(stored.Players);

        // Аккаунты: постоянные номера вместо хеша ника. Реестр засеваем всем, кого
        // сервер уже знал (справочник, баны, кастомные ID), чтобы старые данные можно
        // было перевести на номера ещё до первого входа.
        _accounts = new AccountStore(
            System.IO.Path.Combine(AppContext.BaseDirectory, "accounts.json"));

        var seeded = _accounts.SeedFromPlayers(BuildAccountSeeds(stored));
        if (seeded > 0)
            Console.WriteLine($"[account] засеяно из старых данных: {seeded} аккаунтов");

        // Числовой ID в admin.ids после перенумерации может достаться другому человеку.
        // Не угадываем: пропускаем перенумерацию и просим заменить значение на хеш ника.
        // Пустой реестр перенумеровывать нечего, а уже перенумерованный файл трогать поздно.
        if (_accounts.NeedsRenumber && _accounts.Count > 0 && HasUnsafeNumericAdminIds())
            _accounts.BlockRenumber("admin.ids содержит числовой ID в диапазоне аккаунтов");

        var preferred = ResolvePreferredAdminAccountIds();
        _accounts.RenumberIfNeeded(preferred);

        // Точный ник — личность. Старые файлы могли накопить несколько аккаунтов
        // с одной и той же строкой: склеиваем их в один, иначе лобби, катка и сайт
        // снова разойдутся по разным номерам. Админский номер не теряем.
        var mergedNicks = _accounts.MergeDuplicateNicks(_config.IsAdmin);
        if (mergedNicks > 0)
            Console.WriteLine($"[account] после склейки дублей: {_accounts.Count} аккаунтов");

        _accounts.Save();
        _accounts.Changed = OnAccountsChanged;

        if (_accounts.HasPendingRenumber)
        {
            // Разовая перенумерация: каждый шаг отмечаем только после записи файла.
            if (!_accounts.IsRenumberStepDone("directory"))
            {
                var remapped = _directory.RemapAccountIds(MapRenumberedAccountId);
                if (remapped > 0)
                    Console.WriteLine(
                        $"[account] перенумерация: справочник — {remapped} записей");
                PersistBans();
                _accounts.MarkRenumberStepDone("directory");
            }

            if (!_accounts.IsRenumberStepDone("custom"))
            {
                var custom = _customIds.RemapAccounts(MapRenumberedAccountId);
                if (custom > 0)
                    Console.WriteLine(
                        $"[account] перенумерация: кастомные ID — {custom} записей");
                _accounts.MarkRenumberStepDone("custom");
            }

            if (!_accounts.IsRenumberStepDone("bans"))
            {
                var bans = _bans.RemapAccountIds(MapRenumberedAccountId);
                if (bans > 0)
                    Console.WriteLine(
                        $"[account] перенумерация: теги банов — {bans} записей");
                PersistBans();
                _accounts.MarkRenumberStepDone("bans");
            }
        }
        else
        {
            // Обычная миграция старых хешей ника на аккаунты (файл уже v2 или
            // перенумерация не нужна).
            var remapped = _directory.RemapAccountIds(MapLegacyAccountId);
            if (remapped > 0)
                Console.WriteLine($"[account] справочник переведён на аккаунты: {remapped} записей");

            var custom = _customIds.RemapAccounts(MapLegacyAccountId);
            if (custom > 0)
                Console.WriteLine($"[account] кастомные ID переведены на аккаунты: {custom} записей");

            var bans = _bans.RemapAccountIds(MapLegacyAccountId);
            if (bans > 0)
                Console.WriteLine($"[account] теги банов переведены на аккаунты: {bans} записей");
        }

        MigrateAdminIds();
        PersistBans();

        _bans.Changed = PersistBans;
        _directory.Changed = PersistBans;

        // Матч проверяет те же записи: IP на коннекте, ник до выдачи актора.
        _match.Bans = _bans;
        _match.Accounts = _accounts;
        _match.IsBanExempt = IsBanExemptNick;
        _match.IsBanEnabled = () => _config.Admin.Ban.Enabled;
        _match.BanSweepSeconds = () => _config.Admin.Ban.MatchSweepSec;
        _match.ExposeServerUid = settings.ExposeIds;

        Console.WriteLine(
            $"[ban] реестр: {_bans.Count} банов, {_directory.Count} личностей в справочнике, файл {path}, срок " +
            $"{settings.MinMinutes}м..{settings.MaxDays}д, проверка каждые {settings.SweepSec}с, " +
            $"матч — каждые {settings.MatchSweepSec}с, " +
            $"device-бан {(settings.ByDevice ? "вкл" : "выкл")}, админов банить нельзя");
    }

    /// <summary>
    /// Ники, из которых надо завести аккаунты при первом запуске новой схемы:
    /// справочник, старые баны с алиасами и записи кастомных ID.
    /// </summary>
    private List<SeenPlayer> BuildAccountSeeds(
        (List<BanRecord> Bans, List<SeenPlayer> Players) stored)
    {
        var seeds = new List<SeenPlayer>(stored.Players);
        var seen = stored.Players
            .Select(p => ServerConfig.NormalizeNick(p.Nick))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        void AddSeed(string? nick, long lastSeenUtcMs)
        {
            var normalized = ServerConfig.NormalizeNick(nick);
            if (normalized.Length == 0 || !seen.Add(normalized))
                return;
            seeds.Add(new SeenPlayer
            {
                Nick = normalized,
                LastSeenUtcMs = lastSeenUtcMs,
            });
        }

        foreach (var ban in stored.Bans)
        {
            AddSeed(ban.Nick, ban.CreatedUtcMs);
            foreach (var alias in ban.Aliases)
                AddSeed(alias, ban.CreatedUtcMs);
        }

        foreach (var (_, nick, _) in _customIds.Export())
            AddSeed(nick, 0);

        return seeds;
    }

    /// <summary>
    /// Есть ли в <c>admin.ids</c> числовой номер в диапазоне аккаунтов, который не
    /// является старым хешем ника. После перенумерации такой номер может достаться
    /// другому человеку, поэтому перенумерацию безопаснее пропустить до правки конфига.
    /// </summary>
    private bool HasUnsafeNumericAdminIds()
    {
        var accounts = _accounts;
        if (accounts is null)
            return false;

        foreach (var id in _config.Admin.Ids)
        {
            if (id is < AccountStore.FirstId or > AccountStore.MaxId)
                continue;

            // Номер уже занят этим аккаунтом: перенумерация его сохранит, выдавать
            // чужому нечего.
            if (accounts.FindById(id) is not null)
                continue;

            // 1000000 — владелец по умолчанию. Его аккаунт получает первый номер
            // при перенумерации (см. ResolvePreferredAdminAccountIds), поэтому это
            // не «литерал, который достанется другому».
            if (id == ConfigMigration.DefaultOwnerId && accounts.Count > 0)
                continue;

            // Старый хеш ника тоже может выглядеть как номер аккаунта — это не литерал.
            if (accounts.FindAccountIdsByLegacyId(id).Count > 0)
                continue;

            Console.WriteLine(
                $"[admin] admin.ids: {id} — числовой ID в диапазоне аккаунтов, но не старый хеш ника. " +
                "Перенумерация аккаунтов пропущена, чтобы не выдать права чужому. " +
                "Замени значение на старый хеш ника (например, 25354325) или удали его и перезапусти сервер.");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Аккаунты админов (владельца) для перенумерации: они получают первые номера.
    /// Регистр и алиасы учитываются, как в <see cref="MigrateAdminIds"/>.
    /// </summary>
    private List<int> ResolvePreferredAdminAccountIds()
    {
        var result = new List<int>();
        var accounts = _accounts;
        if (accounts is null)
            return result;

        foreach (var id in _config.Admin.Ids)
        {
            // 1000000 — владелец по умолчанию, но его аккаунт ещё может носить
            // старый номер. Отдаём первый номер владельцу прежнего дефолта
            // (25354325), а если такого нет — самому свежему аккаунту: владелец
            // заходил позже всех.
            if (id == ConfigMigration.DefaultOwnerId && accounts.FindById(id) is null)
            {
                var owner = accounts.FindAccountIdsByLegacyId(ConfigMigration.LegacyOwnerId)
                        .Select(accounts.FindById)
                        .Where(a => a is not null)
                        .OrderByDescending(a => a!.LastSeenUtcMs)
                        .ThenByDescending(a => a!.Id)
                        .FirstOrDefault()
                    ?? accounts.Export()
                        .OrderByDescending(a => a.LastSeenUtcMs)
                        .ThenByDescending(a => a.Id)
                        .FirstOrDefault();

                if (owner is not null && !result.Contains(owner.Id))
                    result.Add(owner.Id);
                continue;
            }

            if (id is >= AccountStore.FirstId and <= AccountStore.MaxId
                && accounts.FindById(id) is { } byId)
            {
                if (!result.Contains(byId.Id))
                    result.Add(byId.Id);
                continue;
            }

            foreach (var account in accounts.FindAccountIdsByLegacyId(id)
                         .Select(accounts.FindById)
                         .Where(a => a is not null)
                         .OrderByDescending(a => a!.LastSeenUtcMs)
                         .ThenByDescending(a => a!.Id))
            {
                if (!result.Contains(account!.Id))
                    result.Add(account.Id);
            }
        }

        return result;
    }

    /// <summary>
    /// Точный перевод старого номера при перенумерации. В отличие от
    /// <see cref="MapLegacyAccountId"/>, сначала смотрит сохранённую карту: после
    /// перенумерации старый номер может уже принадлежать другому аккаунту.
    /// </summary>
    internal int MapRenumberedAccountId(int oldId, string? nick = null)
    {
        // Карту спрашиваем у реестра на каждом вызове: после завершения миграции она
        // удаляется, и старый номер больше нельзя переводить повторно.
        var accounts = _accounts;
        if (accounts is not null && accounts.HasPendingRenumber)
        {
            var mapped = accounts.MapRenumberedId(oldId);
            if (mapped != 0)
                return mapped;
        }

        return MapLegacyAccountId(oldId, nick);
    }

    /// <summary>То же без ника — для аватаров.</summary>
    internal int MapRenumberedAccountId(int oldId) => MapRenumberedAccountId(oldId, null);

    /// <summary>
    /// Старый id (хеш ника) → account ID. Повторяет тот же путь, что миграция
    /// справочника, и запоминает пару для аватаров и других файлов без ника.
    /// </summary>
    internal int MapLegacyAccountId(int oldId, string? nick)
    {
        var accounts = _accounts;
        if (accounts is null || oldId <= 0)
            return 0;

        // Номер настоящего аккаунта не пересчитываем: иначе повторная миграция
        // перепривязала бы уже переведённые записи к чужому тёзке. Но старый хеш
        // ника мог случайно попасть в диапазон номеров — тогда ник записи не
        // принадлежит этому аккаунту, и запись всё-таки legacy.
        if (oldId is >= AccountStore.FirstId and <= AccountStore.MaxId
            && accounts.FindById(oldId) is { } byId
            && (string.IsNullOrWhiteSpace(nick) || byId.HasCurrentNick(nick)))
            return oldId;

        lock (_legacyMapGate)
        {
            if (_legacyAccountIds.TryGetValue(oldId, out var known))
                return known;
        }

        var mapped = accounts.MapLegacyId(oldId, nick);
        if (mapped is < AccountStore.FirstId or > AccountStore.MaxId)
            return 0;

        if (mapped != oldId)
        {
            lock (_legacyMapGate)
                _legacyAccountIds[oldId] = mapped;
        }
        return mapped;
    }

    /// <summary>То же, но без ника: только уже известная карта (для аватаров).</summary>
    internal int MapLegacyAccountId(int oldId) => MapLegacyAccountId(oldId, null);

    private void OnAccountsChanged()
    {
        _accounts?.Save();
        MigrateAdminIds();
        SyncAdminStore();
    }

    /// <summary>
    /// Заменить старые хеши в <c>admin.ids</c> и <c>admin.megaIds</c> на account ID.
    /// Работает в памяти: файл с комментариями не переписываем, при следующем старте
    /// миграция повторится и снова найдёт уже существующий аккаунт. Пока аккаунта нет
    /// (человек ещё не заходил), старый хеш остаётся и продолжает работать через
    /// fallback в <c>IdOf</c>. После замены вызывающий обязан сделать
    /// <see cref="SyncAdminStore"/>, иначе запись и пароль останутся под старым id.
    /// </summary>
    private void MigrateAdminIds()
    {
        var accounts = _accounts;
        if (accounts is null)
            return;

        lock (_adminIdsGate)
        {
            var ids = _config.Admin.Ids;
            var megaIds = _config.Admin.MegaIds;
            if (ids.Count == 0 && megaIds.Count == 0)
                return;

            // Склеенный номер мог быть в admin.ids: права переезжают на канонический.
            foreach (var id in ids.ToList())
            {
                var mapped = accounts.MapMergedId(id);
                if (mapped == 0 || mapped == id)
                    continue;
                ReplaceAdminId(ids, id, mapped);
                Console.WriteLine($"[admin] admin.ids: склеенный {id} → аккаунт {mapped}");
            }

            foreach (var id in megaIds.ToList())
            {
                var mapped = accounts.MapMergedId(id);
                if (mapped == 0 || mapped == id)
                    continue;
                ReplaceAdminId(megaIds, id, mapped);
                Console.WriteLine($"[admin] admin.megaIds: склеенный {id} → аккаунт {mapped}");
            }

            // Права даёт только точный текущий ник. Прежний ник — уже другой аккаунт,
            // он не должен наследовать админку по старому хешу.
            foreach (var account in accounts.Export()
                         .OrderByDescending(a => a.LastSeenUtcMs)
                         .ThenByDescending(a => a.Id))
            {
                var legacy = new[] { account.Nick }
                    .Select(LegacyNickHash.ForNick)
                    .Where(id => id is >= LegacyNickHash.Min and <= LegacyNickHash.Max)
                    .Distinct()
                    .ToList();

                foreach (var oldId in legacy)
                {
                    if (ReplaceAdminId(ids, oldId, account.Id))
                    {
                        Console.WriteLine(
                            $"[admin] admin.ids: старый хеш {oldId} → аккаунт {account.Id} " +
                            $"('{account.Nick}')");
                    }

                    if (ReplaceAdminId(megaIds, oldId, account.Id))
                    {
                        Console.WriteLine(
                            $"[admin] admin.megaIds: старый хеш {oldId} → аккаунт {account.Id} " +
                            $"('{account.Nick}')");
                    }
                }
            }

            DedupeAdminIds(ids);
            DedupeAdminIds(megaIds);
        }
    }

    private static bool ReplaceAdminId(List<int> list, int oldId, int newId)
    {
        var changed = false;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] != oldId)
                continue;
            list[i] = newId;
            changed = true;
        }

        return changed;
    }

    private static void DedupeAdminIds(List<int> list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list.IndexOf(list[i]) != i)
                list.RemoveAt(i);
        }
    }

    private static string ResolveBanPath(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? "banlist.json" : configured.Trim();
        return System.IO.Path.IsPathRooted(name)
            ? name
            : System.IO.Path.Combine(AppContext.BaseDirectory, name);
    }

    private void PersistBans()
    {
        if (_banStore is null || _bans is null)
            return;
        _banStore.Save(_bans.Export(), _directory.Export());
    }

    /// <summary>
    /// Проверка бана по серверному ID игрока — для сайта (#51).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Сайт знает про человека только ID, поэтому решение принимается через справочник
    /// игроков: ID → ник и все адреса, с которых человек заходил в лобби.
    /// </para>
    /// <para>
    /// Вызывается редко (обмен кода и запросы панели), поэтому объект поиска создаётся
    /// на каждый вызов: он крошечный, зато всегда видит текущий реестр.
    /// </para>
    /// </remarks>
    public bool IsPlayerBannedById(int playerId)
    {
        if (_bans is null || !_config.Admin.Ban.Enabled || playerId <= 0)
            return false;

        var lookup = new PlayerBanLookup(_directory, _bans, IsBanExemptNick);
        return lookup.IsBanned(playerId, DateTime.UtcNow);
    }

    /// <summary>Проверка бана на входе в лобби (ник + IP).</summary>
    /// <param name="learn">
    /// Учить новые ключи. Для теневых проверок — false: забаненный не должен
    /// «легализовать» свой новый ник/IP повторными запросами.
    /// </param>
    private BanDecision InspectBan(string? nick, IPAddress? address, bool learn = true) =>
        InspectBan(nick, address, 0, learn);

    /// <summary>Та же проверка, но с номером аккаунта: бан по id ловится и без устройства.</summary>
    private BanDecision InspectBan(
        string? nick,
        IPAddress? address,
        int publicId,
        bool learn = true)
    {
        if (_bans is null || !_config.Admin.Ban.Enabled)
            return BanDecision.Allowed;

        return _bans.Inspect(
            new BanProbe(nick, address?.ToString(), PublicId: publicId),
            DateTime.UtcNow,
            IsBanExemptNick,
            learn);
    }

    /// <summary>
    /// Кого бан не берёт: админа из конфига и сам сервер. Предикат читает <c>_config</c> на
    /// каждом входе, поэтому перечитывание конфига не оставляет старый список админов.
    /// </summary>
    /// <remarks>
    /// Права даёт только точный текущий ник: <see cref="IdOf"/> ищет аккаунт по нему,
    /// а старый хеш — лишь пока аккаунт ещё не создан. Тёзка с другим регистром или
    /// прежний ник админа освобождением от бана не пользуются.
    /// </remarks>
    private bool IsBanExemptNick(string? nick)
    {
        if (ServerIdentity.IsServerNick(nick))
            return true;

        return IsAdminId(IdOf(nick));
    }

    /// <summary>
    /// Скрытый пир: забаненный (теневой сокет) или отклонённый по нику. Чат и команды от
    /// него игнорируются, в росписи и в катке его нет.
    /// </summary>
    private bool IsHiddenPeer(NetPeer peer)
    {
        lock (_shadowGate)
            if (_shadowPeers.ContainsKey(peer))
                return true;

        return IsNickRejected(peer);
    }

    /// <summary>
    /// Забаненный заходит в лобби: успешный JoinResponse с росписью из одного Server,
    /// затем строки уведомления от Server — только ему.
    /// </summary>
    private void HandleShadowJoin(NetPeer peer, JoinLobbyRequest req, BanDecision decision)
    {
        var record = decision.Record;
        if (record is null)
        {
            Send(peer, LobbyCodec.BuildJoinFail(JoinFailReason.NotJoinable));
            return;
        }

        var result = _session.HandleShadowJoinRequest(peer, req);
        if (result.ResponseBytes is not null)
            Send(peer, result.ResponseBytes);

        if (!result.Success)
        {
            Console.WriteLine($"[ban] теневой вход отклонён: {result.FailReason} ({result.Detail})");
            return;
        }

        var nick = ServerConfig.NormalizeNick(req.Profile.Name);
        lock (_shadowGate)
            _shadowPeers[peer] = nick;

        Console.WriteLine(
            $"[ban] теневой вход '{req.Profile.Name}' {peer.Address} — совпало: {decision.MatchedBy}, " +
            $"осталось {BanDuration.FormatRemaining(record.Remaining(DateTime.UtcNow))}" +
            (decision.LearnedAlias ? ", алиас записан" : ""));

        DashboardHub.PostChat(
            LobbySession.ServerPlayerName,
            $"[бан] {req.Profile.Name} ({peer.Address}) — вход закрыт: {decision.MatchedBy}",
            fromServer: true);

        // Строки по порядку: ReliableOrdered доставит их так же, как приветствие.
        foreach (var line in BanMessages.NoticeLines(record, DateTime.UtcNow))
            SendServerChatTo(peer, line);
    }

    /// <summary>
    /// Раз в <c>admin.ban.sweepSec</c> (по умолчанию 5 с): кто получил разбан по сроку —
    /// того сразу отпускает и теневой сокет. Это же чистит banlist.json.
    /// </summary>
    private void SweepBans()
    {
        if (_bans is null)
            return;

        var now = DateTime.UtcNow;
        var every = TimeSpan.FromSeconds(Math.Max(1, _config.Admin.Ban.SweepSec));
        if (now - _lastBanSweepUtc < every)
            return;
        _lastBanSweepUtc = now;

        var removed = _bans.Prune(now);
        if (removed > 0)
            Console.WriteLine($"[ban] срок вышел у {removed} — снято с banlist");

        // Срок мог выйти (или баны выключили) — отпускаем теневые сокеты сразу, не ждём минуты.
        foreach (var (peer, nick) in SnapshotShadowPeers())
        {
            if (InspectBan(nick, peer.Address, learn: false).Banned)
                continue;

            SendServerChatTo(peer, "бан снят — заходи заново");
            try { peer.Disconnect(); }
            catch { /* уже мёртв */ }
            lock (_shadowGate)
                _shadowPeers.Remove(peer);
            Console.WriteLine($"[ban] '{nick}' больше не в бане — теневой сокет закрыт");
        }
    }

    /// <summary>Обновление строки «Разбан через: …» в теневом лобби (раз в минуту).</summary>
    private void RefreshShadowCountdowns()
    {
        if (_bans is null)
            return;

        var now = DateTime.UtcNow;
        var every = TimeSpan.FromSeconds(Math.Max(10, _config.Admin.Ban.ShadowRefreshSec));
        if (now - _lastShadowRefreshUtc < every)
            return;
        _lastShadowRefreshUtc = now;

        foreach (var (peer, nick) in SnapshotShadowPeers())
        {
            var decision = InspectBan(nick, peer.Address, learn: false);
            if (decision.Banned && decision.Record is not null)
                SendServerChatTo(peer, BanMessages.CountdownLine(decision.Record, now));
        }
    }

    /// <summary>
    /// <c>/ban &lt;id&gt; &lt;срок&gt; [причина]</c>. Id — только селектор: баним человека,
    /// то есть его ник + IP, а не само число (оно достанется другому).
    /// </summary>
    private void HandleBanCommand(Action<string> reply, string args, ConnectedPlayer? player)
    {
        if (!BanReady(reply))
            return;

        if (!BanCommandLine.TryParse(args, out var id, out var durationToken, out var reason, out var error))
        {
            reply(error);
            return;
        }

        var target = ResolveBanTargetById(id);
        if (target.Error is not null)
        {
            reply(target.Error);
            return;
        }

        TryBanTarget(
            reply, target.AccountId, target.Nick, target.Address,
            durationToken, reason, player, target.KnownIps);
    }

    /// <summary>
    /// Кого банить по номеру аккаунта: сначала катка, потом лобби, потом справочник.
    /// Общий путь для <c>/ban</c> и веб-админки: правила одни и те же.
    /// </summary>
    /// <remarks>
    /// Номер аккаунта однозначен — тёзок он не путает, поэтому коллизий здесь больше нет.
    /// Номер, которого нет ни в катке, ни в лобби, ни в справочнике, — ошибка: сервер
    /// не выдумывает цель по нику.
    /// </remarks>
    private (int AccountId, string Nick, IPAddress? Address, IReadOnlyList<string>? KnownIps, string? Error)
        ResolveBanTargetById(int id)
    {
        if (id is < AccountStore.FirstId or > AccountStore.MaxId)
        {
            return (0, string.Empty, null, null,
                $"id {id} вне диапазона аккаунтов {AccountStore.FirstId}…{AccountStore.MaxId} " +
                "(список: /ids)");
        }

        // 1) кто сейчас в катке: id аккаунта привязан к актору после успешного JoinRoom.
        var inMatch = _match.SnapshotPublicIds().Where(e => e.Id == id).ToList();
        if (inMatch.Count == 1)
        {
            var entry = inMatch[0];
            return (id, entry.Nick, entry.Address, null, null);
        }
        if (inMatch.Count > 1)
        {
            return (0, string.Empty, null, null, $"id {id} одновременно у {inMatch.Count} акторов катки " +
                   "— это ошибка сервера, бан отменён");
        }

        // 2) кто в лобби: номер выдаётся на входе, поэтому виден и до катки.
        var inLobby = _session.JoinedPlayers()
            .Where(p => p.PublicId == id)
            .ToList();
        if (inLobby.Count > 1)
        {
            return (0, string.Empty, null, null, $"id {id} одновременно у {inLobby.Count} игроков лобби " +
                   "— это ошибка сервера, бан отменён");
        }
        if (inLobby.Count == 1)
        {
            return (id, ServerConfig.NormalizeNick(inLobby[0].Name), inLobby[0].Peer.Address, null, null);
        }

        // 3) уже вышел: справочник помнит ник и все его IP — бан сработает и без него.
        var seen = _directory.Resolve(id);
        if (seen is null)
            return (0, string.Empty, null, null, $"никого с id {id} сервер не помнит — список: /ids");

        return (id, seen.Nick, null, seen.Ips, null);
    }

    private bool BanReady(Action<string> reply)
    {
        if (_bans is null)
        {
            reply("баны не инициализированы");
            return false;
        }

        if (!_config.Admin.Ban.Enabled)
        {
            reply("баны выключены (admin.ban.enabled = false)");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Общее тело <c>/ban</c>: проверки, запись, кик из матча и лобби. Возвращает запись
    /// реестра или <c>null</c>, если бан не состоялся (причина — в <c>reply</c>).
    /// </summary>
    /// <remarks>
    /// <paramref name="bannedBy"/> — кто банит из веб-админки (ник из сессии сайта);
    /// для чат-команд остаётся <c>null</c> — тогда это ник игрока, приславшего команду.
    /// <paramref name="accountId"/> — постоянный номер цели: он и есть ключ бана.
    /// </remarks>
    private BanRecord? TryBanTarget(
        Action<string> reply,
        int accountId,
        string nick,
        IPAddress? address,
        string durationToken,
        string reason,
        ConnectedPlayer? player,
        IReadOnlyList<string>? knownIps = null,
        string? bannedBy = null)
    {
        if (_bans is null)
            return null;

        var settings = _config.Admin.Ban;
        if (nick.Length == 0)
        {
            reply("пустой ник");
            return null;
        }

        if (accountId is < AccountStore.FirstId or > AccountStore.MaxId)
        {
            reply($"id {accountId} вне диапазона аккаунтов {AccountStore.FirstId}…{AccountStore.MaxId}");
            return null;
        }

        // Сам сервер не банится ни по нику, ни по id: это не игрок, а хост.
        if (ServerIdentity.IsServerNick(nick))
        {
            reply($"'{ServerIdentity.Nick}' — это сам сервер, его забанить нельзя");
            return null;
        }

        // Правило №2: админов не баним. Проверяем и номер аккаунта, и старый хеш ника:
        // до первого входа admin.ids ещё может хранить хеш, а аккаунт уже создан.
        if (IsBanExemptNick(nick))
        {
            reply($"'{nick}' — админ (admin.ids), админов банить нельзя");
            Console.WriteLine($"[ban] отказ: '{nick}' (аккаунт {accountId}) админ — банить нельзя");
            return null;
        }

        if (player is not null
            && (player.PublicId == accountId
                || string.Equals(
                    ServerConfig.NormalizeNick(player.Name), nick, StringComparison.OrdinalIgnoreCase)))
        {
            reply("себя банить нельзя");
            return null;
        }

        if (!BanDuration.TryParse(
                durationToken,
                TimeSpan.FromMinutes(settings.MinMinutes),
                TimeSpan.FromDays(settings.MaxDays),
                out var duration,
                out var durationError))
        {
            reply(durationError);
            return null;
        }

        // Цели в лобби: ищем по номеру аккаунта, а не по нику. Тёзка не должен получить
        // кик за чужой бан — ник у двух людей может совпасть, номер аккаунта не может.
        var targets = _session.JoinedPlayers()
            .Where(p => p.PublicId == accountId)
            .ToList();

        var ip = address ?? targets.Select(t => t.Peer.Address).FirstOrDefault(a => a is not null);

        // Устройства: живые сокеты матча + всё, что справочник уже связал с аккаунтом.
        // Живой сокет важнее: он подтверждает, что человек сейчас реально в катке.
        var devices = new List<string>();
        foreach (var liveDevice in _match.DevicesForAccount(accountId))
        {
            if (!devices.Any(d => string.Equals(d, liveDevice, StringComparison.OrdinalIgnoreCase)))
                devices.Add(liveDevice);
        }
        foreach (var knownDevice in _directory.DevicesForAccount(accountId))
        {
            if (!devices.Any(d => string.Equals(d, knownDevice, StringComparison.OrdinalIgnoreCase)))
                devices.Add(knownDevice);
        }

        // Номер аккаунта — постоянный: запись всегда тегируется им, чтобы /unban потом
        // нашёл её без отдельной истории.
        var record = _bans.Add(
            new BanProbe(nick, ip?.ToString(), devices.FirstOrDefault(), accountId),
            duration,
            reason,
            bannedBy ?? player?.Name ?? LobbySession.ServerPlayerName,
            DateTime.UtcNow,
            accountId,
            knownIps,
            devices);

        // Сначала уведомление (в лобби и в катку), потом разрыв: иначе игрок видит
        // только «соединение закрыто» и не понимает, что его забанили.
        foreach (var target in targets)
            SendBanNoticeToLobbyPeer(target.Peer, record);

        // Кик из матча (7777) и из лобби (7778) — разные сокеты, нужны оба.
        var matchKicked = _match.DisconnectBanned(accountId, ip, devices, record);
        var lobbyKicked = 0;
        foreach (var target in targets)
        {
            try
            {
                DisconnectSoon(target.Peer);
                lobbyKicked++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ban] не смог отключить '{target.Name}': {ex.Message}");
            }
        }

        // Теневые сокеты того же нарушителя.
        foreach (var (peer, shadowNick) in SnapshotShadowPeers())
        {
            if (!string.Equals(shadowNick, nick, StringComparison.OrdinalIgnoreCase))
                continue;
            try { peer.Disconnect(); }
            catch { /* уже мёртв */ }
            lock (_shadowGate)
                _shadowPeers.Remove(peer);
        }

        var ips = record.Ips.Count == 0
            ? "IP неизвестен (банить будем по нику)"
            : string.Join(", ", record.Ips);
        reply($"забанен {record.DisplayNick} на {BanDuration.FormatShort(duration)}: {DescribeReason(record)}");
        reply($"бан №{record.BanId} · айди: {accountId}");
        reply($"IP: {ips} · до {record.ExpiresUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
        reply(record.Devices.Count == 0
            ? "устройство: пока неизвестно (игрок ещё не заходил в матч по 2.07)"
            : $"устройство: {string.Join(", ", record.Devices)}");

        Console.WriteLine(
            $"[ban] {record.DisplayNick} на {BanDuration.FormatShort(duration)} причиной " +
            $"'{DescribeReason(record)}' (кто: {record.BannedBy}); бан №{record.BanId}; " +
            $"IP=[{string.Join(", ", record.Ips)}] " +
            $"devices=[{string.Join(", ", record.Devices)}] " +
            $"ids=[{string.Join(", ", record.MatchIds)}]; кик матч={matchKicked}, лобби={lobbyKicked}; " +
            $"выдан {record.CreatedLocalIso} (UTC{BanRecord.CurrentUtcOffsetMinutes() / 60.0:+0.#;-0.#})");

        // Объявление видят все и в лобби, и в катке (BroadcastServerChat идёт в оба канала).
        if (settings.NotifyEveryone)
        {
            foreach (var line in BanMessages.PublicLines(record))
                BroadcastServerChat(line);
        }

        return record;
    }

    private void HandleUnbanCommand(Action<string> reply, string args)
    {
        if (_bans is null)
        {
            reply("баны не инициализированы");
            return;
        }

        var token = (args ?? string.Empty).Trim();
        if (token.Length == 0)
        {
            reply("формат: /unban <id> — id смотри в /ids и в /bans");
            return;
        }

        // Id — основной адрес разбана: снимаем все записи, помеченные этим человеком.
        var digits = token.StartsWith('#') ? token[1..] : token;
        if (int.TryParse(digits, out var id) && id is >= BanCommandLine.MinId and <= BanCommandLine.MaxId)
        {
            RemoveBansById(reply, id);
            return;
        }

        // Разбан по нику или IP оставлен для записей, сделанных до перехода на id.
        var removedOk = _bans.Remove(token, out var removed);
        if (!removedOk || removed is null)
        {
            reply($"в бане нет '{token}'");
            return;
        }

        reply($"разбанен {removed.DisplayNick} (бан №{BanMessages.BanIdOf(removed)}, IP: " +
              $"{(removed.Ips.Count == 0 ? "—" : string.Join(", ", removed.Ips))})");
        Console.WriteLine($"[ban] разбан {removed.DisplayNick} по '{token}' (бан №{BanMessages.BanIdOf(removed)})");
        BroadcastServerChat($"разбанен {removed.DisplayNick}");
    }

    /// <summary>Снять все баны, выданные под этим серверным id. Возвращает сколько снято.</summary>
    private int RemoveBansById(Action<string> reply, int id)
    {
        if (_bans is null)
        {
            reply("баны не инициализированы");
            return 0;
        }

        var removed = _bans.RemoveByMatchId(id);
        if (removed.Count == 0)
        {
            reply($"под id {id} активных банов нет");
            return 0;
        }

        foreach (var record in removed)
        {
            reply($"разбанен {record.DisplayNick} (бан №{BanMessages.BanIdOf(record)}, id {id}, IP: " +
                  $"{(record.Ips.Count == 0 ? "—" : string.Join(", ", record.Ips))})");
            BroadcastServerChat($"разбанен {record.DisplayNick}");
        }

        Console.WriteLine($"[ban] /unban {id}: снято {removed.Count} записей");
        return removed.Count;
    }

    private void HandleBansCommand(Action<string> reply)
    {
        if (_bans is null)
        {
            reply("баны не инициализированы");
            return;
        }

        var now = DateTime.UtcNow;
        var active = _bans.Active(now);
        if (active.Count == 0)
        {
            reply("активных банов нет");
            return;
        }

        reply($"активных банов: {active.Count}");
        foreach (var record in active)
        {
            var ips = record.Ips.Count == 0 ? "—" : string.Join("/", record.Ips);
            var ids = record.MatchIds.Count == 0 ? "" : $" · id {string.Join(",", record.MatchIds)}";
            reply(
                $"{record.DisplayNick} · бан №{BanMessages.BanIdOf(record)} · " +
                $"{BanDuration.FormatRemaining(record.Remaining(now))} · " +
                $"IP {ips}{ids} · {DescribeReason(record)} · забанил {record.BannedBy}");
        }
    }

    /// <summary>
    /// <c>/ids</c> — кто есть на сервере: серверный id, ник, адрес. Список общий для лобби и
    /// катки, потому что баны теперь только по id: админ должен видеть id того, кто ещё не
    /// входил в матч.
    /// </summary>
    private void HandleIdsCommand(Action<string> reply)
    {
        var entries = _match.SnapshotPublicIds();
        foreach (var entry in entries)
        {
            // У актора сервера id — строка «Server», а не число: он вне нумерации.
            var idText = entry.ActorNr == MatchHostActor.ActorNr
                ? ServerIdentity.Uid
                : $"#{entry.Id}";
            reply($"{idText} · {entry.Nick} · {entry.Address?.ToString() ?? "—"} · actor {entry.ActorNr}");
        }

        // Катка идёт, но кто-то сидит в лобби — его id тоже нужен. Дедупликация по
        // номеру аккаунта, а не по нику: тёзка в лобби — отдельный человек.
        var shown = entries.Select(e => e.Id).ToHashSet();
        var lobby = _session.JoinedPlayers()
            .Where(p => p.PublicId > 0 && !shown.Contains(p.PublicId))
            .ToList();

        foreach (var player in lobby)
        {
            reply($"#{player.PublicId} · {player.Name} · " +
                  $"{player.Peer.Address?.ToString() ?? "—"} · лобби");
        }

        if (entries.Count == 0 && lobby.Count == 0)
            reply("в катке и в лобби никого нет");
    }

    private List<(NetPeer Peer, string Nick)> SnapshotShadowPeers()
    {
        lock (_shadowGate)
            return _shadowPeers.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static string DescribeReason(BanRecord record) =>
        string.IsNullOrWhiteSpace(record.Reason) ? "не указана" : record.Reason.Trim();
}
