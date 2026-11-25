namespace StandChillow.LanServer.Auth;

/// <summary>
/// Имена прав админ-панели. Набор фиксирован кодом: права хранятся в
/// <c>auth/admins.json</c> и выдаются только мега-админом, а не конфигом.
/// </summary>
/// <remarks>
/// <para>
/// <c>dashboard</c> и <c>journal</c> есть у любого админа и не отнимаются: без обзора
/// и журнала админ не может понять, что вообще происходит на сервере. Остальные права
/// выдаются поштучно.
/// </para>
/// <para>
/// Мега-админ обходит эту матрицу целиком: его статус задаётся только
/// <c>admin.megaIds</c> в <c>config.json</c>, а не списком <c>permissions</c> и не IP.
/// </para>
/// </remarks>
public static class AdminPermissions
{
    public const string Dashboard = "dashboard";
    public const string Journal = "journal";
    public const string Bans = "bans";
    public const string Reports = "reports";
    public const string Customization = "customization";
    public const string Rooms = "rooms";
    public const string Players = "players";
    public const string Config = "config";
    public const string Admins = "admins";
    public const string Rollback = "rollback";

    /// <summary>Все известные права, включая неотнимаемые.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Dashboard, Journal, Bans, Reports, Customization,
        Rooms, Players, Config, Admins, Rollback,
    };

    /// <summary>Права, которые есть у каждого админа и не отнимаются.</summary>
    public static readonly IReadOnlyList<string> Always = new[]
    {
        Dashboard, Journal,
    };

    /// <summary>Права, которые мега-админ может выдать или отнять.</summary>
    public static readonly IReadOnlyList<string> Assignable = new[]
    {
        Bans, Reports, Customization, Rooms, Players, Config, Admins, Rollback,
    };

    /// <summary>Право известно?</summary>
    public static bool IsKnown(string? permission) =>
        permission is not null && All.Contains(permission, StringComparer.Ordinal);

    /// <summary>Право неотнимаемое?</summary>
    public static bool IsAlways(string? permission) =>
        permission is not null && Always.Contains(permission, StringComparer.Ordinal);

    /// <summary>
    /// Привести список к канону: только известные имена, без дублей, всегда вместе с
    /// <c>dashboard</c> и <c>journal</c>. Порядок — как в <see cref="All"/>.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? requested)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in requested ?? Array.Empty<string>())
        {
            var name = (raw ?? "").Trim().ToLowerInvariant();
            if (name.Length > 0 && IsKnown(name))
                wanted.Add(name);
        }

        foreach (var always in Always)
            wanted.Add(always);

        return All.Where(wanted.Contains).ToList();
    }

    /// <summary>Человеческое описание набора — для консоли и журнала.</summary>
    public static string Describe(IEnumerable<string>? permissions)
    {
        var list = Normalize(permissions);
        return list.Count == 0 ? "(нет)" : string.Join(", ", list);
    }
}
