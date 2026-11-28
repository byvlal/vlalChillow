namespace StandChillow.LanServer.Web;

/// <summary>
/// Обложки режимов и карт для страницы создания комнаты.
/// </summary>
/// <remarks>
/// <para>
/// Ссылки ведут на CDN Fandom (<c>static.wikia.nocookie.net</c>) и отдаются странице
/// как есть — файлы в репозиторий не копируются. Так сайт не распространяет чужие
/// картинки, а вики остаётся их источником. Проверено: CDN отвечает без Referer,
/// а панель уже отправляет <c>Referrer-Policy: no-referrer</c>.
/// </para>
/// <para>
/// Каталог режимов и карт — <see cref="Net.Lobby.GameModeCatalog"/>. Здесь только
/// сопоставление «id режима / имя карты → картинка». Варианты <c>Prison 2x2</c> и
/// <c>Prison 2x2 Alt</c> используют обложку базовой карты.
/// </para>
/// </remarks>
public static class ModeArtCatalog
{
    private const string Cdn = "https://static.wikia.nocookie.net/standoff-2/images/";

    // «Превью — Карта_» и «Превью — Режим_» в percent-encoding: имена файлов на вики
    // кириллические, а ссылка должна остаться рабочей без перекодирования на клиенте.
    private const string MapPrefix =
        "%D0%9F%D1%80%D0%B5%D0%B2%D1%8C%D1%8E_%E2%80%94_%D0%9A%D0%B0%D1%80%D1%82%D0%B0_";

    private const string ModePrefix =
        "%D0%9F%D1%80%D0%B5%D0%B2%D1%8C%D1%8E_%E2%80%94_%D0%A0%D0%B5%D0%B6%D0%B8%D0%BC_";

    private static string Art(string path) => Cdn + path;

    private static readonly Dictionary<string, string> ModeImages = new(StringComparer.Ordinal)
    {
        ["DeathMatch"] = Art("c/c1/" + ModePrefix +
            "%22%D0%9A%D0%BE%D0%BC%D0%B0%D0%BD%D0%B4%D0%BD%D1%8B%D0%B9_%D0%B1%D0%BE%D0%B9%22" +
            "_0.38.0.png/revision/latest/scale-to-width-down/640?cb=20260408130526&path-prefix=ru"),

        ["RankedDefuse"] = Art("8/8c/" + ModePrefix +
            "%22%D0%A1%D0%BE%D1%80%D0%B5%D0%B2%D0%BD%D0%BE%D0%B2%D0%B0%D1%82%D0%B5%D0%BB%D1%8C%D0%BD%D1%8B%D0%B9%22" +
            "_0.38.0.png/revision/latest/scale-to-width-down/364?cb=20260408133616&path-prefix=ru"),

        ["Ranked2v2"] = Art("5/5d/" + ModePrefix +
            "%22%D0%A1%D0%BE%D1%8E%D0%B7%D0%BD%D0%B8%D0%BA%D0%B8%22" +
            "_0.38.0.png/revision/latest/scale-to-width-down/364?cb=20260408133731&path-prefix=ru"),

        ["Ranked2v2Alt"] = Art("4/43/" + ModePrefix +
            "%22%D0%A1%D0%BE%D1%8E%D0%B7%D0%BD%D0%B8%D0%BA%D0%B8_%D0%90%D0%BB%D1%8C%D1%82%D0%B5%D1%80%D0%BD%D0%B0%D1%82%D0%B8%D0%B2%D0%B0%22" +
            "_0.38.0.png/revision/latest/scale-to-width-down/497?cb=20260408142910&path-prefix=ru"),

        ["Defuse"] = Art("a/a8/" + ModePrefix +
            "%22%D0%97%D0%B0%D0%BA%D0%BB%D0%B0%D0%B4%D0%BA%D0%B0_%D0%B1%D0%BE%D0%BC%D0%B1%D1%8B%22" +
            "_0.38.0.png/revision/latest/scale-to-width-down/640?cb=20260408130959&path-prefix=ru"),

        ["Escalation"] = Art("a/ab/" + ModePrefix +
            "%2824%D0%A7%29_%22%D0%AD%D1%81%D0%BA%D0%B0%D0%BB%D0%B0%D1%86%D0%B8%D1%8F%22" +
            "_0.38.0.png/revision/latest/scale-to-width-down/497?cb=20260408132714&path-prefix=ru"),

        ["Duel"] = Art("0/0e/" + ModePrefix +
            "%22%D0%94%D1%83%D1%8D%D0%BB%D1%8C%22" +
            "_0.38.0.png/revision/latest/scale-to-width-down/364?cb=20260408133430&path-prefix=ru"),

        ["ArmsRace"] = Art("3/35/" + ModePrefix +
            "%22%D0%93%D0%BE%D0%BD%D0%BA%D0%B0_%D0%B2%D0%BE%D0%BE%D1%80%D1%83%D0%B6%D0%B5%D0%BD%D0%B8%D0%B9%22" +
            "_0.38.0.png/revision/latest/scale-to-width-down/497?cb=20260408125206&path-prefix=ru"),

        ["FreeForAll"] = Art("6/63/" + ModePrefix +
            "%2824%D0%A7%29_%22%D0%9F%D1%80%D0%BE%D1%82%D0%B8%D0%B2_%D0%B2%D1%81%D0%B5%D1%85%22" +
            "_0.38.0.png/revision/latest/scale-to-width-down/497?cb=20260408132814&path-prefix=ru"),
    };

    private static readonly Dictionary<string, string> MapImages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arena"] = Art("c/c0/" + MapPrefix + "Arena_0.30.0.png/revision/latest/scale-to-width-down/640?cb=20241107173221&path-prefix=ru"),
        ["Block"] = Art("a/ab/" + MapPrefix + "Block_0.38.0.png/revision/latest/scale-to-width-down/640?cb=20260410155555&path-prefix=ru"),
        ["Breeze"] = Art("2/2e/" + MapPrefix + "Breeze_0.20.0.png/revision/latest/scale-to-width-down/640?cb=20241122151018&path-prefix=ru"),
        ["Bridge"] = Art("6/67/" + MapPrefix + "Bridge_0.35.0.png/revision/latest/scale-to-width-down/640?cb=20250826152441&path-prefix=ru"),
        ["Cableway"] = Art("6/62/" + MapPrefix + "Cableway_0.33.0.png/revision/latest/scale-to-width-down/640?cb=20250507063346&path-prefix=ru"),
        ["Calypso"] = Art("f/f6/" + MapPrefix + "Calypso_0.20.0.png/revision/latest/scale-to-width-down/640?cb=20241109175502&path-prefix=ru"),
        ["Dune"] = Art("3/33/" + MapPrefix + "Dune_0.25.0.png/revision/latest/scale-to-width-down/640?cb=20241122202747&path-prefix=ru"),
        ["Favelas"] = Art("7/73/" + MapPrefix + "Favelas_0.33.0.png/revision/latest/scale-to-width-down/640?cb=20250507150623&path-prefix=ru"),
        ["Hanami"] = Art("d/d8/" + MapPrefix + "Hanami_0.35.0.png/revision/latest/scale-to-width-down/640?cb=20250828120618&path-prefix=ru"),
        ["Hanari"] = Art("0/00/" + MapPrefix + "Hanari_0.35.0.png/revision/latest/scale-to-width-down/640?cb=20250828120740&path-prefix=ru"),
        ["Perimeter"] = Art("d/df/" + MapPrefix + "Perimeter_0.38.0.png/revision/latest/scale-to-width-down/640?cb=20260410155809&path-prefix=ru"),
        ["Pipeline"] = Art("b/b1/" + MapPrefix + "Pipeline_0.33.0.png/revision/latest/scale-to-width-down/640?cb=20250509105330&path-prefix=ru"),
        ["Pool"] = Art("3/3b/" + MapPrefix + "Pool_0.29.0.png/revision/latest/scale-to-width-down/640?cb=20241114161310&path-prefix=ru"),
        ["Prison"] = Art("3/37/" + MapPrefix + "Prison_0.38.0.png/revision/latest/scale-to-width-down/640?cb=20260410163143&path-prefix=ru"),
        ["Province"] = Art("d/dc/" + MapPrefix + "Province_0.30.0.png/revision/latest/scale-to-width-down/640?cb=20241123164800&path-prefix=ru"),
        ["Rust"] = Art("b/b4/" + MapPrefix + "Rust_0.38.0.png/revision/latest/scale-to-width-down/640?cb=20260409105917&path-prefix=ru"),
        ["Sand Yards"] = Art("9/94/" + MapPrefix + "Sand_Yards_0.27.0.png/revision/latest/scale-to-width-down/640?cb=20241114145304&path-prefix=ru"),
        ["Sandyards"] = Art("9/94/" + MapPrefix + "Sand_Yards_0.27.0.png/revision/latest/scale-to-width-down/640?cb=20241114145304&path-prefix=ru"),
        ["Sandstone"] = Art("1/12/" + MapPrefix + "Sandstone_0.27.0.png/revision/latest/scale-to-width-down/640?cb=20241123165338&path-prefix=ru"),
        ["Temple"] = Art("8/8a/" + MapPrefix + "Temple_0.35.2.png/revision/latest/scale-to-width-down/640?cb=20250924121545&path-prefix=ru"),
        ["Training Outside"] = Art("7/75/" + MapPrefix + "Training_Outside_0.11.0.png/revision/latest/scale-to-width-down/640?cb=20241115180713&path-prefix=ru"),
        ["TrainingOutside"] = Art("7/75/" + MapPrefix + "Training_Outside_0.11.0.png/revision/latest/scale-to-width-down/640?cb=20241115180713&path-prefix=ru"),
        ["Village"] = Art("1/1e/" + MapPrefix + "Village_0.30.0.png/revision/latest/scale-to-width-down/640?cb=20241116081907&path-prefix=ru"),
        ["Yard"] = Art("c/c0/" + MapPrefix + "Yard_0.35.0.png/revision/latest/scale-to-width-down/640?cb=20250826152322&path-prefix=ru"),
    };

    /// <summary>Обложка режима или <c>null</c>, если для него картинки нет.</summary>
    public static string? ForMode(string gameModeId) =>
        ModeImages.TryGetValue(gameModeId, out var url) ? url : null;

    /// <summary>
    /// Обложка карты по имени уровня. <c>Prison 2x2</c> и <c>Prison 2x2 Alt</c>
    /// сводятся к <c>Prison</c>; неизвестная карта — <c>null</c> (страница
    /// оставит градиент).
    /// </summary>
    public static string? ForLevel(string level)
    {
        var name = BaseLevel(level);
        return MapImages.TryGetValue(name, out var url) ? url : null;
    }

    private static string BaseLevel(string level)
    {
        var name = level.Trim();
        if (name.EndsWith(" Alt", StringComparison.OrdinalIgnoreCase))
            name = name[..^4].TrimEnd();
        if (name.EndsWith(" 2x2", StringComparison.OrdinalIgnoreCase))
            name = name[..^4].TrimEnd();
        return name;
    }
}
