namespace StandChillow.LanServer.Bans;

/// <summary>
/// Бан по серверному ID игрока: ID → ник, известные адреса и устройства → решение реестра.
/// </summary>
/// <remarks>
/// <para>
/// Сайт знает про человека только его ID — тот самый, что показывается в лобби строкой
/// «Ты … · ID: …» и в катке как <c>uid</c>. Справочник игроков (<see cref="PlayerDirectory"/>)
/// помнит по этому ID ник, адреса и устройства, с которых человек заходил, — их и проверяем.
/// </para>
/// <para>
/// Адрес браузера в расчёт не берётся: он не совпадает с адресом игры (NAT, мобильная
/// сеть, другой интерфейс), и бан по нему задел бы всех за этим NAT — см. README.
/// </para>
/// <para>
/// Класс ничего не знает ни о сокетах, ни о панели: решение принимается по уже готовым
/// записям, поэтому проверяется тестами без сети.
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

        // Сначала ник: бан ставится по нику, и это самый частый случай.
        if (_bans.Inspect(new BanProbe(seen.Nick, null), nowUtc, _isExempt).Banned)
            return true;

        // Потом все адреса, которые человек светит в лобби: бан по IP ловит и тех,
        // кто сменил ник (ID — производная от ника, поэтому сменивший ник сменил и ID).
        foreach (var ip in seen.Ips)
        {
            if (string.IsNullOrWhiteSpace(ip))
                continue;

            if (_bans.Inspect(new BanProbe(seen.Nick, ip), nowUtc, _isExempt).Banned)
                return true;
        }

        // И устройства: device-бан не зависит от ника и IP, поэтому именно он ловит
        // «сменил ник и включил VPN». У legacy-записей список пуст — шаг ничего не стоит.
        foreach (var device in seen.Devices)
        {
            if (string.IsNullOrWhiteSpace(device))
                continue;

            if (_bans.Inspect(new BanProbe(seen.Nick, null, device), nowUtc, _isExempt).Banned)
                return true;
        }

        return false;
    }
}
