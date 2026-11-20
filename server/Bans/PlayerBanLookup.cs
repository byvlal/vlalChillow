namespace StandChillow.LanServer.Bans;

/// <summary>
/// Бан по ID аккаунта: ID → ник, известные адреса и устройства → решение реестра.
/// </summary>
/// <remarks>
/// <para>
/// Сайт знает про человека только его ID — тот самый, что показывается в лобби строкой
/// «Ты … · ID: …» и в катке как <c>uid</c>. Справочник игроков (<see cref="PlayerDirectory"/>)
/// помнит по этому ID ник, адреса и устройства, с которых человек заходил, — их и проверяем.
/// </para>
/// <para>
/// Порядок — от жёсткого к мягкому: устройства (userId), адреса, публичный id, ник.
/// Это чтение, а не вход: <c>learn: false</c> обязательно, иначе запрос к сайту приклеил
/// бы чужой ник/IP к бану. ID здесь — найденная запись справочника, а не догадка по нику:
/// поэтому его можно передавать в проверку.
/// </para>
/// <para>
/// Адрес браузера в расчёт не берётся: он не совпадает с адресом игры (NAT, мобильная
/// сеть, другой интерфейс), и бан по нему задел бы всех за этим NAT — см. README.
/// </para>
/// </remarks>
public sealed class PlayerBanLookup
{
    private readonly PlayerDirectory _directory;
    private readonly BanRegistry _bans;
    private readonly Func<string?, bool>? _isExempt;

    /// <param name="directory">Справочник «ID ↔ ник ↔ адреса».</param>
    /// <param name="bans">Реестр банов.</param>
    /// <param name="isExempt">Кого бан не берёт (админ и сам сервер).</param>
    public PlayerBanLookup(
        PlayerDirectory directory,
        BanRegistry bans,
        Func<string?, bool>? isExempt = null)
    {
        _directory = directory;
        _bans = bans;
        _isExempt = isExempt;
    }

    /// <summary>
    /// Забанен ли игрок с таким ID. Неизвестный ID — не забанен: реестр про него молчит,
    /// а молчание баном не считается.
    /// </summary>
    public bool IsBanned(int playerId, DateTime nowUtc)
    {
        if (playerId <= 0)
            return false;

        var seen = _directory.Resolve(playerId);
        if (seen is null)
            return false;

        // userId — главный ключ: не зависит от ника и IP.
        foreach (var device in seen.Devices)
        {
            if (string.IsNullOrWhiteSpace(device))
                continue;

            if (_bans.Inspect(
                    new BanProbe(seen.Nick, null, device, seen.Id),
                    nowUtc,
                    _isExempt,
                    learn: false)
                .Banned)
                return true;
        }

        // IP — жёсткий ключ текущей сети.
        foreach (var ip in seen.Ips)
        {
            if (string.IsNullOrWhiteSpace(ip))
                continue;

            if (_bans.Inspect(
                    new BanProbe(seen.Nick, ip, null, seen.Id),
                    nowUtc,
                    _isExempt,
                    learn: false)
                .Banned)
                return true;
        }

        // Публичный id аккаунта — жёсткий ключ, но только на чтение: запись уже
        // привязана к человеку, а не угадана по нику.
        if (_bans.Inspect(
                new BanProbe(seen.Nick, null, null, seen.Id),
                nowUtc,
                _isExempt,
                learn: false)
            .Banned)
            return true;

        // Ник — мягкий ключ: blocks only this identity, ничего не учит.
        return _bans.Inspect(
                new BanProbe(seen.Nick, null, null, 0),
                nowUtc,
                _isExempt,
                learn: false)
            .Banned;
    }
}
