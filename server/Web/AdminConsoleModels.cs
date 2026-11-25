namespace StandChillow.LanServer.Web;

/// <summary>Строка поиска игрока для админ-консоли (онлайн и офлайн).</summary>
public sealed record AdminPlayerRow(
    int Id,
    string Nick,
    string Status,
    bool Banned,
    string? CustomId,
    DateTime? LastSeenUtc,
    IReadOnlyList<string> Ips,
    IReadOnlyList<string> Devices,
    IReadOnlyList<string> Aliases,
    DateTime? CreatedUtc,
    bool Admin,
    bool Mega,
    string Note = "")
{
    /// <summary>Статус человеческим словом — для журнала и ответа.</summary>
    public string StatusLabel => Status switch
    {
        "match" => "в игре",
        "lobby" => "в лобби",
        "offline" => "не в сети",
        _ => Status,
    };
}

/// <summary>Тип значения безопасного редактора конфига.</summary>
public enum AdminConfigValueKind
{
    String,
    Bool,
    Int,
}

/// <summary>Одно поле, которое панель разрешает менять в config.json.</summary>
public sealed record AdminConfigOption(
    string Key,
    string Label,
    AdminConfigValueKind Kind,
    string Value,
    string Section,
    double? Min = null,
    double? Max = null,
    IReadOnlyList<string>? Choices = null,
    string? Help = null,
    bool RestartRequired = false);

/// <summary>Снимок безопасной части конфига для страницы «Конфиг».</summary>
public sealed record AdminConfigSnapshot(
    string Path,
    IReadOnlyList<AdminConfigOption> Options,
    IReadOnlyList<int> AdminIds,
    IReadOnlyList<int> MegaIds,
    IReadOnlyList<string> Issues,
    DateTime? LastWriteUtc);

/// <summary>Живое состояние сервера для дашборда консоли.</summary>
public sealed record AdminServerState(
    int LobbyOnline,
    int MatchOnline,
    int RoomsCount,
    int RoomsMax,
    bool MatchRunning,
    bool GameListening,
    bool GamePortFailed,
    string Mode,
    IReadOnlyList<string> Levels);
